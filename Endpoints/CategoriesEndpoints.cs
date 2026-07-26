using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class CategoriesEndpoints
{
    internal static void MapCategoriesEndpoints(this WebApplication app)
    {
app.MapGet("/api/categories", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    var popular = userId.HasValue
        ? db.PopularCategoriesLast30Days
            .AsNoTracking()
            .Where(p => p.UserId == userId.Value)
        : db.PopularCategoriesLast30Days
            .AsNoTracking()
            .Where(p => false);

    var categories = await db.Categories
        .AsNoTracking()
        .GroupJoin(
            popular,
            c => c.Id,
            p => p.CategoryId,
            (c, p) => new
            {
                Category = c,
                Popular = p.FirstOrDefault()
            })
        .OrderByDescending(x => x.Popular != null)
        .ThenByDescending(x => x.Popular != null ? x.Popular.TransactionsCount : 0)
        .ThenByDescending(x => x.Popular != null ? x.Popular.TotalAmount : 0)
        .ThenBy(x => x.Category.Id)
        .Select(x => new CategoryResponse(
            x.Category.Id,
            x.Category.Name,
            x.Category.Type == CategoryType.Income ? "income" : "expense",
            x.Category.Type == CategoryType.Income ? "income" : "other"))
        .ToListAsync(ct);

    return Results.Ok(categories);
});

app.MapPost("/api/categories", async (CreateCategoryRequest request, AppDbContext db, CancellationToken ct) =>
{
    var type = NormalizeCategoryType(request.Type);
    if (type is null)
    {
        return Results.BadRequest(new { message = "Type must be 'income' or 'expense'." });
    }

    var name = request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new { message = "Category name is required." });
    }

    var existing = await db.Categories
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower() && c.Type == type.Value, ct);

    if (existing is not null)
    {
        return Results.Ok(new CategoryResponse(
            existing.Id,
            existing.Name,
            existing.Type == CategoryType.Income ? "income" : "expense",
            NormalizeIconKey(null, existing.Type)));
    }

    var category = new Category
    {
        Name = name,
        Type = type.Value
    };

    db.Categories.Add(category);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/categories/{category.Id}", new CategoryResponse(
        category.Id,
        category.Name,
        category.Type == CategoryType.Income ? "income" : "expense",
        NormalizeIconKey(null, category.Type)));
}).RequireAuthorization();

app.MapPatch("/api/categories/{categoryId:int}", async (
    int categoryId,
    UpdateCategoryRequest request,
    AppDbContext db,
    CancellationToken ct) =>
{
    var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
    if (category is null) return Results.NotFound(new { message = "Category does not exist." });

    if (!string.IsNullOrWhiteSpace(request.Name))
    {
        category.Name = request.Name.Trim();
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok(new CategoryResponse(
        category.Id,
        category.Name,
        category.Type == CategoryType.Income ? "income" : "expense",
        NormalizeIconKey(null, category.Type)));
}).RequireAuthorization();

app.MapGet("/api/user-categories", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var preferences = await db.UserCategoryPreferences
        .AsNoTracking()
        .Where(x => x.UserId == userId.Value)
        .ToListAsync(ct);
    var activeIds = preferences
        .Where(x => x.IsActive)
        .Select(x => x.CategoryId)
        .ToList();
    var preferenceIconKeys = preferences
        .Where(x => !string.IsNullOrWhiteSpace(x.IconKey))
        .ToDictionary(x => x.CategoryId, x => x.IconKey);
    var preferenceColors = preferences
        .Where(x => !string.IsNullOrWhiteSpace(x.Color))
        .ToDictionary(x => x.CategoryId, x => x.Color);

    var activeIdSet = activeIds.ToHashSet();
    var hasSavedPreferences = preferences.Count > 0;

    var popular = db.PopularCategoriesLast30Days
        .AsNoTracking()
        .Where(p => p.UserId == userId.Value);

    var categoryRows = await db.Categories
        .AsNoTracking()
        .GroupJoin(
            popular,
            c => c.Id,
            p => p.CategoryId,
            (c, p) => new
            {
                Category = c,
                Popular = p.FirstOrDefault()
            })
        .OrderByDescending(x => x.Popular != null)
        .ThenByDescending(x => x.Popular != null ? x.Popular.TransactionsCount : 0)
        .ThenByDescending(x => x.Popular != null ? x.Popular.TotalAmount : 0)
        .ThenBy(x => x.Category.Id)
        .ToListAsync(ct);

    var categories = categoryRows
        .Select(x => new UserCategoryPreferenceResponse(
            x.Category.Id,
            x.Category.Name,
            x.Category.Type == CategoryType.Income ? "income" : "expense",
            preferenceIconKeys.TryGetValue(x.Category.Id, out var iconKey)
                ? NormalizeIconKey(iconKey, x.Category.Type)
                : NormalizeIconKey(null, x.Category.Type),
            preferenceColors.TryGetValue(x.Category.Id, out var color)
                ? color
                : null,
            !hasSavedPreferences || activeIdSet.Contains(x.Category.Id)))
        .ToList();

    return Results.Ok(categories);
}).RequireAuthorization();

app.MapPut("/api/user-categories", async (
    UpdateUserCategoryPreferencesRequest request,
    ClaimsPrincipal principal,
    AppDbContext db,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var selectedIds = request.SelectedCategoryIds
        .Distinct()
        .ToHashSet();

    var preferenceById = request.Preferences?
        .GroupBy(x => x.CategoryId)
        .ToDictionary(x => x.Key, x => x.Last()) ?? [];

    log.LogInformation(
        "Updating category preferences: UserId={UserId}, SelectedCount={SelectedCount}, PreferencesCount={PreferencesCount}",
        userId.Value,
        selectedIds.Count,
        preferenceById.Count);

    var requestedCategoryIds = preferenceById.Count > 0
        ? selectedIds.Concat(preferenceById.Keys).Distinct().ToHashSet()
        : selectedIds;

    var validCategories = await db.Categories
        .Where(c => requestedCategoryIds.Contains(c.Id))
        .Select(c => new { c.Id, c.Type })
        .ToListAsync(ct);

    var validCategoryIds = validCategories.Select(x => x.Id).ToHashSet();
    if (preferenceById.Count == 0)
    {
        await db.UserCategoryPreferences
            .Where(x => x.UserId == userId.Value && !validCategoryIds.Contains(x.CategoryId))
            .ExecuteDeleteAsync(ct);
    }

    foreach (var category in validCategories)
    {
        preferenceById.TryGetValue(category.Id, out var preference);
        var iconKey = NormalizeIconKey(preference?.IconKey, category.Type);
        var color = string.IsNullOrWhiteSpace(preference?.Color) ? null : preference.Color;
        var isActive = preference?.IsActive ?? selectedIds.Contains(category.Id);

        var updated = await db.UserCategoryPreferences
            .Where(x => x.UserId == userId.Value && x.CategoryId == category.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IconKey, iconKey)
                .SetProperty(x => x.Color, color)
                .SetProperty(x => x.IsActive, isActive), ct);

        if (updated > 0)
        {
            log.LogInformation(
                "Updated category preference: UserId={UserId}, CategoryId={CategoryId}, IconKey={IconKey}, Color={Color}, IsActive={IsActive}, Rows={Rows}",
                userId.Value,
                category.Id,
                iconKey,
                color,
                isActive,
                updated);
            continue;
        }

        db.UserCategoryPreferences.Add(new UserCategoryPreference
        {
            UserId = userId.Value,
            CategoryId = category.Id,
            IconKey = iconKey,
            Color = color,
            IsActive = isActive
        });
        log.LogInformation(
            "Inserted category preference: UserId={UserId}, CategoryId={CategoryId}, IconKey={IconKey}, Color={Color}, IsActive={IsActive}",
            userId.Value,
            category.Id,
            iconKey,
            color,
            isActive);
    }

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/categories/{categoryId:int}", async (
    int categoryId,
    ClaimsPrincipal principal,
    AppDbContext db,
    CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
    if (category is null) return Results.NotFound(new { message = "Category does not exist." });

    var isUsed = await db.Transactions.AnyAsync(t => t.CategoryId == categoryId, ct) ||
        await db.Budgets.AnyAsync(b => b.CategoryId == categoryId, ct);
    if (isUsed)
    {
        return Results.Conflict(new { message = "Category is used by transactions or budgets. Merge it into another category before deleting." });
    }

    var preferences = await db.UserCategoryPreferences
        .Where(x => x.CategoryId == categoryId)
        .ToListAsync(ct);
    db.UserCategoryPreferences.RemoveRange(preferences);
    db.Categories.Remove(category);

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/categories/{sourceCategoryId:int}/merge", async (
    int sourceCategoryId,
    MergeCategoryRequest request,
    ClaimsPrincipal principal,
    AppDbContext db,
    CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    if (sourceCategoryId == request.TargetCategoryId)
    {
        return Results.BadRequest(new { message = "Choose a different target category." });
    }

    var source = await db.Categories.FirstOrDefaultAsync(c => c.Id == sourceCategoryId, ct);
    var target = await db.Categories.FirstOrDefaultAsync(c => c.Id == request.TargetCategoryId, ct);
    if (source is null || target is null)
    {
        return Results.NotFound(new { message = "Source or target category does not exist." });
    }

    if (source.Type != target.Type)
    {
        return Results.BadRequest(new { message = "Categories must have the same type." });
    }

    await using var tx = await db.Database.BeginTransactionAsync(ct);

    var transactions = await db.Transactions
        .Where(t => t.CategoryId == sourceCategoryId)
        .ToListAsync(ct);
    foreach (var transaction in transactions)
    {
        transaction.CategoryId = target.Id;
    }

    var sourceBudgets = await db.Budgets
        .Where(b => b.CategoryId == sourceCategoryId)
        .ToListAsync(ct);
    foreach (var budget in sourceBudgets)
    {
        budget.CategoryId = target.Id;
    }

    var sourcePreferences = await db.UserCategoryPreferences
        .Where(x => x.CategoryId == sourceCategoryId)
        .ToListAsync(ct);
    db.UserCategoryPreferences.RemoveRange(sourcePreferences);

    var preferenceUserIds = sourcePreferences.Select(x => x.UserId).Distinct().ToArray();
    var existingTargetPreferenceUserIds = await db.UserCategoryPreferences
        .Where(x => x.CategoryId == target.Id && preferenceUserIds.Contains(x.UserId))
        .Select(x => x.UserId)
        .ToListAsync(ct);
    var missingPreferenceUserIds = preferenceUserIds.Except(existingTargetPreferenceUserIds).ToArray();
    db.UserCategoryPreferences.AddRange(missingPreferenceUserIds.Select(preferenceUserId => new UserCategoryPreference
    {
        UserId = preferenceUserId,
        CategoryId = target.Id
    }));

    db.Categories.Remove(source);
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);

    return Results.Ok(new CategoryResponse(
        target.Id,
        target.Name,
        target.Type == CategoryType.Income ? "income" : "expense",
        NormalizeIconKey(null, target.Type)));
}).RequireAuthorization();

    }
}
