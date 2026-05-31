using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Extensions;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.MapEnum<UserRole>("user_role");
            npgsqlOptions.MapEnum<UserGroupRole>("user_group_role");
            npgsqlOptions.MapEnum<CategoryType>("category_type");
        }));
builder.Services.AddScoped<PasswordHasher>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<RefreshTokenService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is missing.");
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is missing.");

var accessCookieName = builder.Configuration["Auth:AccessCookieName"] ?? "access_token";
var refreshCookieName = builder.Configuration["Auth:RefreshCookieName"] ?? "refresh_token";
var refreshCookiePath = builder.Configuration["Auth:RefreshCookiePath"] ?? "/api/auth";
var cookieSameSiteMode = ParseSameSiteMode(builder.Configuration["Auth:CookieSameSite"]);
var useSecureCookies = bool.TryParse(builder.Configuration["Auth:UseSecureCookies"], out var secureCookiesParsed)
    ? secureCookiesParsed
    : !builder.Environment.IsDevelopment();

if (cookieSameSiteMode == SameSiteMode.None && !useSecureCookies)
{
    throw new InvalidOperationException("SameSite=None requires secure cookies.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue(accessCookieName, out var cookieToken))
                {
                    context.Token = cookieToken;
                    return Task.CompletedTask;
                }

                if (string.IsNullOrWhiteSpace(context.Token) &&
                    context.Request.Headers.TryGetValue("Authorization", out StringValues authHeader))
                {
                    var token = authHeader.ToString().Trim();
                    while (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        token = token["Bearer ".Length..].Trim();
                    }

                    context.Token = token.Trim('"');
                }

                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { message = "Unauthorized" });
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.Claims.Any(c =>
                    (c.Type == ClaimTypes.Role || c.Type == "role") &&
                    string.Equals(c.Value, "admin", StringComparison.OrdinalIgnoreCase))));
});

await EnsureDatabaseAndSchemaAsync(builder.Configuration, builder.Environment, CancellationToken.None);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseErrorHandlingMiddleware();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    var userId = GetUserIdFromPrincipal(context.User);
    if (!userId.HasValue)
    {
        await next();
        return;
    }

    var db = context.RequestServices.GetRequiredService<AppDbContext>();
    if (!db.Database.IsRelational())
    {
        await next();
        return;
    }

    await db.Database.OpenConnectionAsync(context.RequestAborted);
    try
    {
        await SetAuditContextAsync(db, userId.Value, GetRequesterIp(context), context.RequestAborted);
        await next();
    }
    finally
    {
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.current_user_id', '', false), set_config('app.device', '', false);");
        }

        await db.Database.CloseConnectionAsync();
    }
});

app.MapGet("/api/roles", () => Results.Ok(new[]
{
    new { Name = "admin", Description = "Administrator" },
    new { Name = "user", Description = "Regular user" }
}));

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

app.MapGet("/api/budgets", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var budgetStartDate = DateOnly.FromDateTime(DateTime.UtcNow);
    var budgets = await db.Budgets
        .AsNoTracking()
        .Where(b => b.Account != null && (b.Account.UserId == userId.Value ||
            (b.GroupId != null && db.GroupMembers.Any(m => m.GroupId == b.GroupId && m.UserId == userId.Value)))
        )
        .OrderBy(b => b.CategoryId)
        .Select(b => new BudgetResponse(
            b.Id,
            b.Account != null ? b.Account.UserId : userId.Value,
            b.GroupId,
            b.CategoryId,
            b.Amount,
            null,
            budgetStartDate,
            true))
        .ToListAsync(ct);

    return Results.Ok(budgets);
}).RequireAuthorization();

app.MapPut("/api/budgets/category/{categoryId:int}", async (
    int categoryId,
    UpsertBudgetRequest request,
    ClaimsPrincipal principal,
    AppDbContext db,
    CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var hasCategory = await db.Categories.AnyAsync(c => c.Id == categoryId, ct);
    if (!hasCategory) return Results.BadRequest(new { message = "Category does not exist." });

    if (request.GroupId.HasValue)
    {
        var canManageGroupBudget = await db.GroupMembers.AnyAsync(
            m => m.GroupId == request.GroupId.Value &&
                 m.UserId == userId.Value &&
                 m.Role != UserGroupRole.Viewer,
            ct);
        if (!canManageGroupBudget) return Results.NotFound();
    }

    var account = await db.BankAccounts
        .Where(a => request.GroupId.HasValue
            ? a.GroupId == request.GroupId.Value
            : a.UserId == userId.Value && a.GroupId == null)
        .OrderByDescending(a => a.IsDefault)
        .ThenBy(a => a.Name)
        .FirstOrDefaultAsync(ct);

    if (account is null)
    {
        return Results.BadRequest(new { message = "Create an account before setting a budget." });
    }

    var budget = await db.Budgets.FirstOrDefaultAsync(
        b => b.AccountId == account.Id &&
             b.CategoryId == categoryId &&
             b.GroupId == request.GroupId,
        ct);

    if (budget is null)
    {
        budget = new Budget
        {
            AccountId = account.Id,
            GroupId = request.GroupId,
            CategoryId = categoryId
        };
        db.Budgets.Add(budget);
    }

    budget.Amount = request.Amount;

    await db.SaveChangesAsync(ct);
    budget.Account = account;
    return Results.Ok(ToBudgetResponse(budget));
}).RequireAuthorization();

app.MapGet("/api/users", async (AppDbContext db, CancellationToken ct) =>
{
    var users = await db.Users
        .AsNoTracking()
        .Select(u => new UserResponse(u.Id, u.Role == UserRole.Admin ? "admin" : "user", u.Username, u.Email, u.FullName, u.IsActive, u.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/users/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users
        .AsNoTracking()
        .Where(u => u.Id == id)
        .Select(u => new UserResponse(u.Id, u.Role == UserRole.Admin ? "admin" : "user", u.Username, u.Email, u.FullName, u.IsActive, u.CreatedAt))
        .FirstOrDefaultAsync(ct);

    return user is null ? Results.NotFound() : Results.Ok(user);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/users/search", async (string? q, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var query = (q ?? string.Empty).Trim().ToLowerInvariant();
    var users = await db.Users
        .AsNoTracking()
        .Where(u => u.Id != userId.Value && u.IsActive)
        .Where(u => string.IsNullOrWhiteSpace(query) ||
            u.Username.ToLower().Contains(query) ||
            u.Email.ToLower().Contains(query) ||
            (u.FullName != null && u.FullName.ToLower().Contains(query)))
        .OrderBy(u => u.Username)
        .Take(25)
        .Select(u => new UserResponse(u.Id, u.Role == UserRole.Admin ? "admin" : "user", u.Username, u.Email, u.FullName, u.IsActive, u.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(users);
}).RequireAuthorization();

app.MapPost("/api/users", async (CreateUserRequest request, AppDbContext db, PasswordHasher hasher, CancellationToken ct) =>
{
    var role = NormalizeRole(request.Role);
    if (role is null)
    {
        return Results.BadRequest(new { message = "Role must be 'admin' or 'user'." });
    }

    var user = new User
    {
        Role = role.Value,
        Username = request.Username.Trim(),
        Email = request.Email.Trim().ToLowerInvariant(),
        PasswordHash = hasher.Hash(request.Password),
        FullName = request.FullName?.Trim()
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/users/{user.Id}", new UserResponse(
        user.Id, ToRoleName(user.Role), user.Username, user.Email, user.FullName, user.IsActive, user.CreatedAt));
}).RequireAuthorization("AdminOnly");

app.MapPatch("/api/users/{id:guid}", async (Guid id, UpdateUserRequest request, ClaimsPrincipal principal, AppDbContext db, PasswordHasher hasher, CancellationToken ct) =>
{
    var actorUserId = GetUserIdFromPrincipal(principal);
    if (!actorUserId.HasValue) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    if (request.Role is not null)
    {
        var role = NormalizeRole(request.Role);
        if (role is null)
        {
            return Results.BadRequest(new { message = "Role must be 'admin' or 'user'." });
        }

        user.Role = role.Value;
    }

    if (request.Username is not null) user.Username = request.Username.Trim();
    if (request.Email is not null) user.Email = request.Email.Trim().ToLowerInvariant();
    if (request.Password is not null) user.PasswordHash = hasher.Hash(request.Password);
    if (request.FullName is not null) user.FullName = request.FullName.Trim();
    if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/users/{id:guid}", async (Guid id, UpdateUserRequest request, ClaimsPrincipal principal, AppDbContext db, PasswordHasher hasher, CancellationToken ct) =>
{
    var actorUserId = GetUserIdFromPrincipal(principal);
    if (!actorUserId.HasValue) return Results.Unauthorized();

    if (request.Role is null ||
        string.IsNullOrWhiteSpace(request.Username) ||
        string.IsNullOrWhiteSpace(request.Email) ||
        string.IsNullOrWhiteSpace(request.Password) ||
        !request.IsActive.HasValue)
    {
        return Results.BadRequest(new { message = "Role, Username, Email, Password and IsActive are required for PUT." });
    }

    var role = NormalizeRole(request.Role);
    if (role is null)
    {
        return Results.BadRequest(new { message = "Role must be 'admin' or 'user'." });
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    user.Role = role.Value;
    user.Username = request.Username.Trim();
    user.Email = request.Email.Trim().ToLowerInvariant();
    user.PasswordHash = hasher.Hash(request.Password);
    user.FullName = request.FullName?.Trim();
    user.IsActive = request.IsActive.Value;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/users/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var actorUserId = GetUserIdFromPrincipal(principal);
    if (!actorUserId.HasValue) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    db.Users.Remove(user);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/groups", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var groupRows = await db.GroupMembers
        .AsNoTracking()
        .Where(m => m.UserId == userId.Value)
        .Join(db.Groups,
            m => m.GroupId,
            g => g.Id,
            (m, g) => new { Member = m, Group = g })
        .GroupJoin(db.GroupMembers,
            x => x.Group.Id,
            member => member.GroupId,
            (x, members) => new
            {
                x.Group.Id,
                x.Group.Name,
                x.Group.IconKey,
                x.Group.CreatedAt,
                x.Member.Role,
                MemberCount = members.Count()
            })
        .OrderBy(g => g.Name)
        .Select(g => new GroupResponse(
            g.Id,
            g.Name,
            g.IconKey ?? "other",
            g.Role == UserGroupRole.Member ? "member" : g.Role == UserGroupRole.Viewer ? "viewer" : "owner",
            g.CreatedAt,
            g.MemberCount))
        .ToListAsync(ct);

    var groups = groupRows
        .Select(g => g with { IconKey = NormalizeGroupIconKey(g.IconKey) })
        .ToList();

    return Results.Ok(groups);
}).RequireAuthorization();

app.MapPost("/api/groups", async (CreateGroupRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var group = new Group
    {
        Name = request.Name.Trim(),
        IconKey = NormalizeGroupIconKey(request.IconKey)
    };

    if (string.IsNullOrWhiteSpace(group.Name))
    {
        return Results.BadRequest(new { message = "Group name is required." });
    }

    db.Groups.Add(group);
    await db.SaveChangesAsync(ct);

    var member = new GroupMember
    {
        GroupId = group.Id,
        UserId = userId.Value,
        Role = UserGroupRole.Owner
    };

    db.GroupMembers.Add(member);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/groups/{group.Id}", new GroupResponse(group.Id, group.Name, NormalizeGroupIconKey(group.IconKey), "owner", group.CreatedAt, 1));
}).RequireAuthorization();

app.MapPatch("/api/groups/{id:guid}", async (Guid id, UpdateGroupRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    var name = request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new { message = "Group name is required." });
    }

    var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
    if (group is null) return Results.NotFound();

    group.Name = name;
    if (request.IconKey is not null)
    {
        group.IconKey = NormalizeGroupIconKey(request.IconKey);
    }
    await db.SaveChangesAsync(ct);

    var memberCount = await db.GroupMembers.CountAsync(m => m.GroupId == id, ct);
    return Results.Ok(new GroupResponse(group.Id, group.Name, NormalizeGroupIconKey(group.IconKey), "owner", group.CreatedAt, memberCount));
}).RequireAuthorization();

app.MapGet("/api/groups/{id:guid}/members", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (!isMember) return Results.NotFound();

    var members = await db.GroupMembers
        .AsNoTracking()
        .Where(m => m.GroupId == id)
        .Join(db.Users,
            m => m.UserId,
            u => u.Id,
            (m, u) => new { Member = m, User = u })
        .OrderBy(x => x.User.Username)
        .Select(x => new GroupMemberResponse(
                x.Member.GroupId,
                x.User.Id,
                x.User.Username,
                x.User.FullName,
                x.Member.Role == UserGroupRole.Member ? "member" : x.Member.Role == UserGroupRole.Viewer ? "viewer" : "owner",
                x.Member.JoinedAt))
        .ToListAsync(ct);

    return Results.Ok(members);
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/members", async (Guid id, AddGroupMemberRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role is UserGroupRole.Viewer) return Results.NotFound();

    var login = request.LoginOrEmail.Trim();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == login || u.Email == login, ct);
    if (user is null) return Results.NotFound();

    var existing = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == user.Id, ct);
    var role = ParseGroupRole(request.RoleName);

    if (existing is null)
    {
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = id,
            UserId = user.Id,
            Role = role,
            JoinedAt = DateTimeOffset.UtcNow
        });
    }
    else
    {
        existing.Role = role;
    }

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/groups/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
    if (group is null) return Results.NotFound();

    var bankAccounts = await db.BankAccounts.Where(a => a.GroupId == id).ToListAsync(ct);
    foreach (var account in bankAccounts)
    {
        account.GroupId = null;
    }

    var savings = await db.Savings.Where(s => s.GroupId == id).ToListAsync(ct);
    foreach (var saving in savings)
    {
        saving.GroupId = null;
    }

    var transactions = await db.Transactions.Where(t => t.GroupId == id).ToListAsync(ct);
    foreach (var transaction in transactions)
    {
        transaction.GroupId = null;
    }

    var members = await db.GroupMembers.Where(m => m.GroupId == id).ToListAsync(ct);
    db.GroupMembers.RemoveRange(members);
    db.Groups.Remove(group);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPatch("/api/groups/{id:guid}/members/{memberUserId:guid}", async (Guid id, Guid memberUserId, UpdateGroupMemberRoleRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == memberUserId, ct);
    if (member is null) return Results.NotFound();
    if (member.Role == UserGroupRole.Owner) return Results.BadRequest(new { message = "Transfer ownership to change the owner role." });

    var role = ParseGroupRole(request.RoleName);
    if (role == UserGroupRole.Owner) return Results.BadRequest(new { message = "Use owner transfer endpoint." });

    member.Role = role;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/transfer-owner", async (Guid id, TransferGroupOwnerRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();
    if (request.NewOwnerUserId == userId.Value) return Results.BadRequest(new { message = "You are already the owner." });

    var newOwner = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == request.NewOwnerUserId, ct);
    if (newOwner is null) return Results.NotFound();

    requester.Role = UserGroupRole.Member;
    newOwner.Role = UserGroupRole.Owner;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/groups/{id:guid}/members/{memberUserId:guid}", async (Guid id, Guid memberUserId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    if (memberUserId == userId.Value)
    {
        return Results.BadRequest(new { message = "You cannot remove yourself from the group here." });
    }

    var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == memberUserId, ct);
    if (member is null) return Results.NotFound();
    if (member.Role == UserGroupRole.Owner) return Results.BadRequest(new { message = "Transfer ownership before removing the owner." });

    db.GroupMembers.Remove(member);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/logs/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var groupUserIds = await db.GroupMembers
        .AsNoTracking()
        .Where(m => db.GroupMembers.Any(my => my.GroupId == m.GroupId && my.UserId == userId.Value))
        .Select(m => m.UserId)
        .Distinct()
        .ToListAsync(ct);

    if (!groupUserIds.Contains(userId.Value))
    {
        groupUserIds.Add(userId.Value);
    }

    var logs = await db.AuditLogs
        .AsNoTracking()
        .Where(l => l.UserId != null && groupUserIds.Contains(l.UserId.Value))
        .GroupJoin(db.Users,
            l => l.UserId,
            u => u.Id,
            (log, users) => new { log, user = users.FirstOrDefault() })
        .OrderByDescending(x => x.log.CreatedAt)
        .Take(100)
        .Select(x => new AuditLogResponse(
            x.log.Id,
            x.log.UserId,
            x.user == null ? null : x.user.Username,
            x.log.Action,
            x.log.Details,
            x.log.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(logs);
}).RequireAuthorization();

app.MapPost("/api/auth/register", async (
    HttpContext context,
    RegisterRequest request,
    AppDbContext db,
    PasswordHasher hasher,
    JwtTokenService jwtTokenService,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    var username = request.Username.Trim();
    var usernameLower = username.ToLowerInvariant();
    var email = request.Email.Trim().ToLowerInvariant();

    var exists = await db.Users.AnyAsync(
        u => u.Email == email || u.Username.ToLower() == usernameLower,
        ct);

    if (exists)
    {
        return Results.Conflict(new { message = "User with this username or email already exists." });
    }

    await using var registrationTx = await db.Database.BeginTransactionAsync(ct);
    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7301985);", ct);
    var isFirstUser = !await db.Users.AsNoTracking().AnyAsync(ct);

    var user = new User
    {
        Id = Guid.NewGuid(),
        Role = isFirstUser ? UserRole.Admin : UserRole.User,
        Username = username,
        Email = email,
        PasswordHash = hasher.Hash(request.Password),
        FullName = request.FullName?.Trim(),
        IsActive = true
    };

    await SetAuditContextAsync(db, user.Id, GetRequesterIp(context), ct);
    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    await registrationTx.CommitAsync(ct);

    var accessTokenResult = jwtTokenService.CreateToken(user, ToRoleName(user.Role));
    var refreshTokenResult = refreshTokenService.CreateToken(user.Id, GetRequesterIp(context));

    await SetAuditContextAsync(db, user.Id, GetRequesterIp(context), ct);
    db.RefreshTokens.Add(refreshTokenResult.StoredToken);
    await db.SaveChangesAsync(ct);

    AppendAuthCookies(context, accessCookieName, refreshCookieName, accessTokenResult.Token, accessTokenResult.ExpiresAt,
        refreshTokenResult.RawToken, refreshTokenResult.StoredToken.ExpiresAt, useSecureCookies, cookieSameSiteMode, refreshCookiePath);

    return Results.Created($"/api/users/{user.Id}", new AuthResponse(
        accessTokenResult.Token, accessTokenResult.ExpiresAt, refreshTokenResult.StoredToken.ExpiresAt,
        user.Id, user.Username, user.Email, ToRoleName(user.Role)));
});

app.MapPost("/api/auth/login", async (
    HttpContext context,
    LoginRequest request,
    AppDbContext db,
    PasswordHasher hasher,
    JwtTokenService jwtTokenService,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    var login = request.Login.Trim();
    var loginLower = login.ToLowerInvariant();

    var user = await db.Users
        .AsNoTracking()
        .FirstOrDefaultAsync(u => u.Email == loginLower || u.Username.ToLower() == loginLower, ct);

    if (user is null || !user.IsActive || !hasher.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var accessTokenResult = jwtTokenService.CreateToken(user, ToRoleName(user.Role));
    var refreshTokenResult = refreshTokenService.CreateToken(user.Id, GetRequesterIp(context));

    await SetAuditContextAsync(db, user.Id, GetRequesterIp(context), ct);
    db.RefreshTokens.Add(refreshTokenResult.StoredToken);
    await db.SaveChangesAsync(ct);

    AppendAuthCookies(context, accessCookieName, refreshCookieName, accessTokenResult.Token, accessTokenResult.ExpiresAt,
        refreshTokenResult.RawToken, refreshTokenResult.StoredToken.ExpiresAt, useSecureCookies, cookieSameSiteMode, refreshCookiePath);

    return Results.Ok(new AuthResponse(
        accessTokenResult.Token, accessTokenResult.ExpiresAt, refreshTokenResult.StoredToken.ExpiresAt,
        user.Id, user.Username, user.Email, ToRoleName(user.Role)));
});

app.MapPost("/api/auth/refresh", async (
    HttpContext context,
    AppDbContext db,
    JwtTokenService jwtTokenService,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    if (!context.Request.Cookies.TryGetValue(refreshCookieName, out var rawRefreshToken) ||
        string.IsNullOrWhiteSpace(rawRefreshToken))
    {
        return Results.Unauthorized();
    }

    var refreshTokenHash = refreshTokenService.Hash(rawRefreshToken);
    var nowUtc = DateTimeOffset.UtcNow;
    var currentToken = await db.RefreshTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.TokenHash == refreshTokenHash, ct);

    if (currentToken?.User is null)
    {
        return Results.Unauthorized();
    }

    if (!refreshTokenService.IsActive(currentToken, nowUtc) || !currentToken.User.IsActive)
    {
        return Results.Unauthorized();
    }

    var newAccessToken = jwtTokenService.CreateToken(currentToken.User, ToRoleName(currentToken.User.Role));
    var newRefreshToken = refreshTokenService.CreateToken(currentToken.User.Id, GetRequesterIp(context));

    await SetAuditContextAsync(db, currentToken.User.Id, GetRequesterIp(context), ct);
    var deletedTokens = await db.RefreshTokens
        .Where(t => t.Id == currentToken.Id)
        .ExecuteDeleteAsync(ct);
    if (deletedTokens == 0)
    {
        ClearAuthCookies(context, accessCookieName, refreshCookieName, useSecureCookies, cookieSameSiteMode, refreshCookiePath);
        return Results.Unauthorized();
    }

    db.RefreshTokens.Add(newRefreshToken.StoredToken);
    await db.SaveChangesAsync(ct);

    AppendAuthCookies(context, accessCookieName, refreshCookieName, newAccessToken.Token, newAccessToken.ExpiresAt,
        newRefreshToken.RawToken, newRefreshToken.StoredToken.ExpiresAt, useSecureCookies, cookieSameSiteMode, refreshCookiePath);

    return Results.Ok(new RefreshResponse(newAccessToken.Token, newAccessToken.ExpiresAt, newRefreshToken.StoredToken.ExpiresAt));
});

app.MapPost("/api/auth/logout", async (
    HttpContext context,
    AppDbContext db,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    if (context.Request.Cookies.TryGetValue(refreshCookieName, out var rawRefreshToken) &&
        !string.IsNullOrWhiteSpace(rawRefreshToken))
    {
        var tokenHash = refreshTokenService.Hash(rawRefreshToken);
        var storedToken = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (storedToken is not null)
        {
            await SetAuditContextAsync(db, storedToken.UserId, GetRequesterIp(context), ct);
            db.RefreshTokens.Remove(storedToken);
            await db.SaveChangesAsync(ct);
        }
    }

    ClearAuthCookies(context, accessCookieName, refreshCookieName, useSecureCookies, cookieSameSiteMode, refreshCookiePath);
    return Results.Ok();
});

app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue)
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
    if (user is null || !user.IsActive)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        user.Id,
        user.Username,
        user.Email,
        user.FullName,
        user.IsActive,
        RoleName = ToRoleName(user.Role),
        user.CreatedAt
    });
}).RequireAuthorization();

app.MapGet("/api/bank-accounts", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var accounts = await db.BankAccounts
        .AsNoTracking()
        .Where(a => a.UserId == userId.Value ||
            (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value)))
        .OrderByDescending(a => a.IsDefault)
        .ThenBy(a => a.Name)
        .Select(a => new BankAccountResponse(a.Id, a.UserId, a.GroupId, a.Name, a.Currency, a.Balance, a.IsDefault))
        .ToListAsync(ct);

    return Results.Ok(accounts);
}).RequireAuthorization();

app.MapGet("/api/bank-accounts/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts
        .AsNoTracking()
        .Where(a => a.Id == id && (a.UserId == userId.Value ||
            (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value))))
        .Select(a => new BankAccountResponse(a.Id, a.UserId, a.GroupId, a.Name, a.Currency, a.Balance, a.IsDefault))
        .FirstOrDefaultAsync(ct);

    return account is null ? Results.NotFound() : Results.Ok(account);
}).RequireAuthorization();

app.MapPost("/api/bank-accounts", async (CreateBankAccountRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    if (request.IsDefault)
    {
        await ClearDefaultBankAccountsAsync(db, userId.Value, ct);
    }

    var account = new BankAccount
    {
        UserId = userId.Value,
        Name = request.Name.Trim(),
        Currency = request.Currency.Trim().ToUpperInvariant(),
        Balance = request.Balance,
        IsDefault = request.IsDefault,
        GroupId = request.GroupId
    };

    db.BankAccounts.Add(account);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/bank-accounts/{account.Id}", ToBankAccountResponse(account));
}).RequireAuthorization();

app.MapPut("/api/bank-accounts/{id:guid}", async (Guid id, UpdateBankAccountRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && (a.UserId == userId.Value ||
        (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (account is null) return Results.NotFound();

    if (request.IsDefault)
    {
        await ClearDefaultBankAccountsAsync(db, userId.Value, ct);
    }

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    var previousGroupId = account.GroupId;
    account.Name = request.Name.Trim();
    account.Currency = request.Currency.Trim().ToUpperInvariant();
    account.Balance = request.Balance;
    account.IsDefault = request.IsDefault;
    account.GroupId = request.GroupId;

    var accountTransactions = await db.Transactions.Where(t => t.AccountId == account.Id).ToListAsync(ct);
    foreach (var transaction in accountTransactions)
    {
        if (transaction.GroupId is null || transaction.GroupId == previousGroupId)
        {
            transaction.GroupId = request.GroupId;
        }
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok(ToBankAccountResponse(account));
}).RequireAuthorization();

app.MapDelete("/api/bank-accounts/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId.Value, ct);
    if (account is null) return Results.NotFound();

    db.BankAccounts.Remove(account);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPut("/api/bank-accounts/{id:guid}/group", async (Guid id, ShareResourceWithGroupRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId.Value, ct);
    if (account is null) return Results.NotFound();

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    account.GroupId = request.GroupId;

    var transactions = await db.Transactions.Where(t => t.AccountId == account.Id).ToListAsync(ct);
    foreach (var transaction in transactions)
    {
        transaction.GroupId = request.GroupId;
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok(ToBankAccountResponse(account));
}).RequireAuthorization();

app.MapGet("/api/savings", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var savings = await db.Savings
        .AsNoTracking()
        .Where(s => s.UserId == userId.Value ||
            (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value)))
        .OrderBy(s => s.IsCompleted)
        .ThenBy(s => s.Deadline)
        .ThenBy(s => s.Name)
        .Select(s => new SavingResponse(s.Id, s.UserId, s.GroupId, s.Name, s.TargetAmount, s.CurrentAmount, s.Deadline, s.Currency, s.IconKey, s.Color, s.IsCompleted))
        .ToListAsync(ct);

    return Results.Ok(savings);
}).RequireAuthorization();

app.MapGet("/api/savings/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = await db.Savings
        .AsNoTracking()
        .Where(s => s.Id == id && (s.UserId == userId.Value ||
            (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value))))
        .Select(s => new SavingResponse(s.Id, s.UserId, s.GroupId, s.Name, s.TargetAmount, s.CurrentAmount, s.Deadline, s.Currency, s.IconKey, s.Color, s.IsCompleted))
        .FirstOrDefaultAsync(ct);

    return saving is null ? Results.NotFound() : Results.Ok(saving);
}).RequireAuthorization();

app.MapPost("/api/savings", async (CreateSavingRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = new Saving
    {
        UserId = userId.Value,
        Name = request.Name.Trim(),
        Currency = NormalizeCurrency(request.Currency),
        IconKey = NormalizeSavingIconKey(request.IconKey),
        Color = NormalizeColor(request.Color),
        TargetAmount = request.TargetAmount,
        CurrentAmount = request.CurrentAmount,
        Deadline = request.Deadline,
        IsCompleted = request.TargetAmount.HasValue && request.CurrentAmount >= request.TargetAmount.Value
    };

    db.Savings.Add(saving);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/savings/{saving.Id}", ToSavingResponse(saving));
}).RequireAuthorization();

app.MapPut("/api/savings/{id:int}", async (int id, UpdateSavingRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = await db.Savings.FirstOrDefaultAsync(s => s.Id == id && (s.UserId == userId.Value ||
        (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (saving is null) return Results.NotFound();

    saving.Name = request.Name.Trim();
    saving.Currency = NormalizeCurrency(request.Currency);
    saving.IconKey = NormalizeSavingIconKey(request.IconKey);
    saving.Color = NormalizeColor(request.Color);
    saving.TargetAmount = request.TargetAmount;
    saving.CurrentAmount = request.CurrentAmount;
    saving.Deadline = request.Deadline;
    saving.IsCompleted = request.IsCompleted;

    await db.SaveChangesAsync(ct);
    return Results.Ok(ToSavingResponse(saving));
}).RequireAuthorization();

app.MapDelete("/api/savings/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = await db.Savings.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId.Value, ct);
    if (saving is null) return Results.NotFound();

    db.Savings.Remove(saving);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPut("/api/savings/{id:int}/group", async (int id, ShareResourceWithGroupRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = await db.Savings.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId.Value, ct);
    if (saving is null) return Results.NotFound();

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    saving.GroupId = request.GroupId;
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToSavingResponse(saving));
}).RequireAuthorization();

app.MapGet("/api/savings/{savingId:int}/items", async (int savingId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownsSaving = await db.Savings.AnyAsync(s => s.Id == savingId && (s.UserId == userId.Value ||
        (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value))), ct);
    if (!ownsSaving) return Results.NotFound();

    var items = await db.SavingItems
        .AsNoTracking()
        .Where(i => i.SavingId == savingId)
        .OrderBy(i => i.IsPurchased)
        .ThenBy(i => i.Priority)
        .Select(i => new SavingItemResponse(i.Id, i.SavingId, i.Name, i.Price, i.Priority, i.IsPurchased))
        .ToListAsync(ct);

    return Results.Ok(items);
}).RequireAuthorization();

app.MapPost("/api/savings/{savingId:int}/items", async (int savingId, CreateSavingItemRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownsSaving = await db.Savings.AnyAsync(s => s.Id == savingId && (s.UserId == userId.Value ||
        (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (!ownsSaving) return Results.NotFound();

    var item = new SavingItem
    {
        SavingId = savingId,
        Name = request.Name.Trim(),
        Price = request.Price,
        Priority = request.Priority,
        IsPurchased = request.IsPurchased
    };

    db.SavingItems.Add(item);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/savings/{savingId}/items/{item.Id}", ToSavingItemResponse(item));
}).RequireAuthorization();

app.MapPut("/api/savings/{savingId:int}/items/{itemId:int}", async (int savingId, int itemId, UpdateSavingItemRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownsSaving = await db.Savings.AnyAsync(s => s.Id == savingId && (s.UserId == userId.Value ||
        (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (!ownsSaving) return Results.NotFound();

    var item = await db.SavingItems.FirstOrDefaultAsync(i => i.Id == itemId && i.SavingId == savingId, ct);
    if (item is null) return Results.NotFound();

    item.Name = request.Name.Trim();
    item.Price = request.Price;
    item.Priority = request.Priority;
    item.IsPurchased = request.IsPurchased;

    await db.SaveChangesAsync(ct);
    return Results.Ok(ToSavingItemResponse(item));
}).RequireAuthorization();

app.MapDelete("/api/savings/{savingId:int}/items/{itemId:int}", async (int savingId, int itemId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownsSaving = await db.Savings.AnyAsync(s => s.Id == savingId && (s.UserId == userId.Value ||
        (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (!ownsSaving) return Results.NotFound();

    var item = await db.SavingItems.FirstOrDefaultAsync(i => i.Id == itemId && i.SavingId == savingId, ct);
    if (item is null) return Results.NotFound();

    db.SavingItems.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/planned-transactions", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transactionRows = await db.Transactions
        .AsNoTracking()
        .Where(t => t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value))))
        .OrderBy(t => t.TransactionDate)
        .Join(db.ScheduledPayments,
            t => t.RecurringPaymentId!.Value,
            p => p.Id,
            (t, p) => new
            {
                t.Id,
                t.AccountId,
                t.CategoryId,
                RecurringPaymentId = p.Id,
                p.Name,
                t.Amount,
                t.Description,
                t.TransactionDate,
                p.RepeatInterval,
                p.NextDueDate,
                p.IsActive
            })
        .ToListAsync(ct);

    var transactions = transactionRows
        .Select(x => new PlannedTransactionResponse(
            x.Id,
            x.AccountId,
            x.CategoryId,
            x.RecurringPaymentId,
            x.Name,
            x.Amount,
            x.Description,
            x.TransactionDate,
            x.RepeatInterval,
            ToDateOnly(x.NextDueDate),
            x.IsActive))
        .ToList();

    return Results.Ok(transactions);
}).RequireAuthorization();

app.MapPost("/api/planned-transactions", async (CreatePlannedTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    var ownsAccount = account is not null;
    if (!ownsAccount) return Results.BadRequest(new { message = "Account does not exist." });

    var hasCategory = await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct);
    if (!hasCategory) return Results.BadRequest(new { message = "Category does not exist." });

    var payment = new ScheduledPayment
    {
        Name = request.Name.Trim(),
        RepeatInterval = request.RepeatInterval,
        NextDueDate = ToUtcDateTimeOffset(request.NextDueDate),
        IsActive = true
    };

    db.ScheduledPayments.Add(payment);
    await db.SaveChangesAsync(ct);

    var transaction = new Transaction
    {
        AccountId = request.AccountId,
        GroupId = account!.GroupId,
        CategoryId = request.CategoryId,
        RecurringPaymentId = payment.Id,
        Amount = request.Amount,
        Description = string.IsNullOrWhiteSpace(request.Description) ? request.Name.Trim() : request.Description.Trim(),
        TransactionDate = new DateTimeOffset(request.NextDueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
    };

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/planned-transactions/{transaction.Id}", ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapPut("/api/planned-transactions/{id:int}", async (int id, CreatePlannedTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (account is null) return Results.BadRequest(new { message = "Account does not exist." });

    var hasCategory = await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct);
    if (!hasCategory) return Results.BadRequest(new { message = "Category does not exist." });

    var recurringPaymentId = transaction.RecurringPaymentId!.Value;
    var payment = await db.ScheduledPayments.FirstOrDefaultAsync(p => p.Id == recurringPaymentId, ct);
    if (payment is null) return Results.NotFound();

    payment.Name = request.Name.Trim();
    payment.RepeatInterval = request.RepeatInterval;
    payment.NextDueDate = ToUtcDateTimeOffset(request.NextDueDate);
    payment.IsActive = true;

    transaction.AccountId = request.AccountId;
    transaction.GroupId = account.GroupId;
    transaction.CategoryId = request.CategoryId;
    transaction.Amount = request.Amount;
    transaction.Description = string.IsNullOrWhiteSpace(request.Description) ? request.Name.Trim() : request.Description.Trim();
    transaction.TransactionDate = ToUtcDateTimeOffset(request.NextDueDate);

    await db.SaveChangesAsync(ct);
    return Results.Ok(ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapPost("/api/planned-transactions/{id:int}/confirm", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();

    var recurringPaymentId = transaction.RecurringPaymentId!.Value;
    var payment = await db.ScheduledPayments.FirstOrDefaultAsync(p => p.Id == recurringPaymentId, ct);
    if (payment is null || !payment.IsActive)
    {
        transaction.RecurringPaymentId = null;
        transaction.TransactionDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToTransactionResponse(transaction));
    }

    if (payment.RepeatInterval > TimeSpan.Zero)
    {
        var nextDueDate = AddRepeatInterval(ToDateOnly(payment.NextDueDate), payment.RepeatInterval);
        payment.NextDueDate = ToUtcDateTimeOffset(nextDueDate);
        db.Transactions.Add(new Transaction
        {
            AccountId = transaction.AccountId,
            GroupId = transaction.GroupId,
            CategoryId = transaction.CategoryId,
            RecurringPaymentId = payment.Id,
            Amount = transaction.Amount,
            Description = transaction.Description,
            TransactionDate = ToUtcDateTimeOffset(nextDueDate)
        });
    }
    else
    {
        payment.IsActive = false;
    }

    transaction.RecurringPaymentId = null;
    transaction.TransactionDate = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapDelete("/api/planned-transactions/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();

    var recurringPaymentId = transaction.RecurringPaymentId!.Value;
    db.Transactions.Remove(transaction);

    var hasOtherPlannedTransactions = await db.Transactions
        .AnyAsync(t => t.Id != id && t.RecurringPaymentId == recurringPaymentId, ct);
    if (!hasOtherPlannedTransactions)
    {
        var payment = await db.ScheduledPayments.FirstOrDefaultAsync(p => p.Id == recurringPaymentId, ct);
        if (payment is not null)
        {
            db.ScheduledPayments.Remove(payment);
        }
    }

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/transactions", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transactions = await db.Transactions
        .AsNoTracking()
        .Where(t => t.RecurringPaymentId == null && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.GroupId && m.UserId == userId.Value)) ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value))))
        .OrderByDescending(t => t.TransactionDate)
        .Select(t => new TransactionResponse(
            t.Id,
            t.AccountId,
            t.GroupId,
            t.Group != null ? t.Group.Name : null,
            t.Account != null && t.Account.User != null ? t.Account.User.Username : null,
            t.CategoryId,
            t.SavingId,
            t.RecurringPaymentId,
            t.Amount,
            t.Description,
            t.TransactionDate))
        .ToListAsync(ct);

    return Results.Ok(transactions);
}).RequireAuthorization();

app.MapGet("/api/transactions/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .AsNoTracking()
        .Where(t => t.Id == id && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.GroupId && m.UserId == userId.Value)) ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value))))
        .Select(t => new TransactionResponse(
            t.Id,
            t.AccountId,
            t.GroupId,
            t.Group != null ? t.Group.Name : null,
            t.Account != null && t.Account.User != null ? t.Account.User.Username : null,
            t.CategoryId,
            t.SavingId,
            t.RecurringPaymentId,
            t.Amount,
            t.Description,
            t.TransactionDate))
        .FirstOrDefaultAsync(ct);

    return transaction is null ? Results.NotFound() : Results.Ok(transaction);
}).RequireAuthorization();

app.MapPost("/api/transactions", async (CreateTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transactionAccount = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transactionAccount is null) return Results.BadRequest(new { message = "Account does not exist." });

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    if (request.SavingId.HasValue)
    {
        var canUseSaving = await db.Savings.AnyAsync(s => s.Id == request.SavingId.Value && (s.UserId == userId.Value ||
            (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
        if (!canUseSaving) return Results.BadRequest(new { message = "Saving does not exist." });
    }

    var transaction = new Transaction
    {
        AccountId = request.AccountId,
        GroupId = request.GroupId ?? transactionAccount.GroupId,
        CategoryId = request.CategoryId,
        SavingId = request.SavingId,
        RecurringPaymentId = request.RecurringPaymentId,
        Amount = request.Amount,
        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
        TransactionDate = request.TransactionDate.ToUniversalTime()
    };

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/transactions/{transaction.Id}", ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapPut("/api/transactions/{id:int}", async (int id, UpdateTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();

    var targetAccount = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        (a.GroupId != null && db.GroupMembers.Any(m => m.GroupId == a.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (targetAccount is null) return Results.BadRequest(new { message = "Account does not exist." });

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    if (request.SavingId.HasValue)
    {
        var canUseSaving = await db.Savings.AnyAsync(s => s.Id == request.SavingId.Value && (s.UserId == userId.Value ||
            (s.GroupId != null && db.GroupMembers.Any(m => m.GroupId == s.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
        if (!canUseSaving) return Results.BadRequest(new { message = "Saving does not exist." });
    }

    transaction.AccountId = request.AccountId;
    transaction.GroupId = request.GroupId ?? targetAccount.GroupId;
    transaction.CategoryId = request.CategoryId;
    transaction.SavingId = request.SavingId;
    transaction.RecurringPaymentId = request.RecurringPaymentId;
    transaction.Amount = request.Amount;
    transaction.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
    transaction.TransactionDate = request.TransactionDate.ToUniversalTime();
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapDelete("/api/transactions/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.Account != null && (t.Account.UserId == userId.Value ||
            (t.Account.GroupId != null && db.GroupMembers.Any(m => m.GroupId == t.Account.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();

    db.Transactions.Remove(transaction);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.Run();

static Guid? GetUserIdFromPrincipal(ClaimsPrincipal principal)
{
    var userIdRaw =
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
        principal.FindFirstValue("sub");

    return Guid.TryParse(userIdRaw, out var userId) ? userId : null;
}

static UserRole? NormalizeRole(string? role)
{
    var normalized = role?.Trim().ToLowerInvariant();
    return normalized switch
    {
        "admin" => UserRole.Admin,
        "user" => UserRole.User,
        _ => null
    };
}

static CategoryType? NormalizeCategoryType(string? type)
{
    var normalized = type?.Trim().ToLowerInvariant();
    return normalized switch
    {
        "income" => CategoryType.Income,
        "expense" => CategoryType.Expense,
        _ => null
    };
}

static string NormalizeIconKey(string? iconKey, CategoryType categoryType)
{
    var normalized = (iconKey ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return categoryType == CategoryType.Income ? "income" : "other";
    }

    return normalized.Length > 50 ? normalized[..50] : normalized;
}

static string NormalizeGroupIconKey(string? iconKey)
{
    var normalized = (iconKey ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "other";
    }

    return normalized.Length > 50 ? normalized[..50] : normalized;
}

static string NormalizeSavingIconKey(string? iconKey)
{
    var normalized = (iconKey ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "other";
    }

    return normalized.Length > 50 ? normalized[..50] : normalized;
}

static string NormalizeCurrency(string? currency)
{
    var normalized = (currency ?? "UAH").Trim().ToUpperInvariant();
    return normalized.Length == 3 ? normalized : "UAH";
}

static string? NormalizeColor(string? color)
{
    var normalized = (color ?? string.Empty).Trim();
    return string.IsNullOrWhiteSpace(normalized)
        ? null
        : normalized.Length > 10 ? normalized[..10] : normalized;
}

static string ToRoleName(UserRole role) =>
    role == UserRole.Admin ? "admin" : "user";

static BankAccountResponse ToBankAccountResponse(BankAccount account) =>
    new(account.Id, account.UserId, account.GroupId, account.Name, account.Currency, account.Balance, account.IsDefault);

static SavingResponse ToSavingResponse(Saving saving) =>
    new(saving.Id, saving.UserId, saving.GroupId, saving.Name, saving.TargetAmount, saving.CurrentAmount, saving.Deadline, saving.Currency, saving.IconKey, saving.Color, saving.IsCompleted);

static SavingItemResponse ToSavingItemResponse(SavingItem item) =>
    new(item.Id, item.SavingId, item.Name, item.Price, item.Priority, item.IsPurchased);

static DateOnly AddRepeatInterval(DateOnly dueDate, TimeSpan repeatInterval)
{
    var date = dueDate.ToDateTime(TimeOnly.MinValue);
    return repeatInterval.TotalDays switch
    {
        >= 28 and <= 31 => DateOnly.FromDateTime(date.AddMonths(1)),
        >= 365 and <= 366 => DateOnly.FromDateTime(date.AddYears(1)),
        _ => DateOnly.FromDateTime(date.Add(repeatInterval))
    };
}

static DateOnly ToDateOnly(DateTimeOffset value) =>
    DateOnly.FromDateTime(value.UtcDateTime);

static DateTimeOffset ToUtcDateTimeOffset(DateOnly value) =>
    new(value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

static TransactionResponse ToTransactionResponse(Transaction transaction) =>
    new(transaction.Id, transaction.AccountId, transaction.GroupId, transaction.Group?.Name, transaction.Account?.User?.Username, transaction.CategoryId, transaction.SavingId, transaction.RecurringPaymentId, transaction.Amount,
        transaction.Description, transaction.TransactionDate);

static BudgetResponse ToBudgetResponse(Budget budget) =>
    new(
        budget.Id,
        budget.Account?.UserId ?? Guid.Empty,
        budget.GroupId,
        budget.CategoryId,
        budget.Amount,
        null,
        DateOnly.FromDateTime(DateTime.UtcNow),
        true);

static async Task ClearDefaultBankAccountsAsync(AppDbContext db, Guid userId, CancellationToken ct)
{
    var defaultAccounts = await db.BankAccounts
        .Where(a => a.UserId == userId && a.IsDefault)
        .ToListAsync(ct);

    foreach (var defaultAccount in defaultAccounts)
    {
        defaultAccount.IsDefault = false;
    }
}

static UserGroupRole ParseGroupRole(string roleName) =>
    roleName.Trim().ToLowerInvariant() switch
    {
        "owner" => UserGroupRole.Owner,
        "viewer" => UserGroupRole.Viewer,
        _ => UserGroupRole.Member
    };

static SameSiteMode ParseSameSiteMode(string? configuredValue) =>
    configuredValue?.Trim().ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax
    };

static string? GetRequesterIp(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString();

static async Task SetAuditContextAsync(AppDbContext db, Guid userId, string? device, CancellationToken ct)
{
    if (!db.Database.IsRelational())
    {
        return;
    }

    await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT set_config('app.current_user_id', {userId.ToString()}, false), set_config('app.device', {device ?? string.Empty}, false);",
        ct);
}

static async Task EnsureDatabaseAndSchemaAsync(IConfiguration configuration, IHostEnvironment environment, CancellationToken ct)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("DefaultConnection is missing.");
    }

    var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString);
    var databaseName = connectionStringBuilder.Database;
    if (string.IsNullOrWhiteSpace(databaseName))
    {
        throw new InvalidOperationException("DefaultConnection must include a Database name.");
    }

    var masterBuilder = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Database = "postgres"
    };

    await using (var masterConnection = new NpgsqlConnection(masterBuilder.ConnectionString))
    {
        await masterConnection.OpenAsync(ct);

        await using (var existsCommand = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", masterConnection))
        {
            existsCommand.Parameters.AddWithValue("name", databaseName);
            var exists = await existsCommand.ExecuteScalarAsync(ct) is not null;
            if (!exists)
            {
                var quotedName = QuoteIdentifier(databaseName);
                await using var createCommand = new NpgsqlCommand($"CREATE DATABASE {quotedName}", masterConnection);
                await createCommand.ExecuteNonQueryAsync(ct);
            }
        }
    }

    await using var dbConnection = new NpgsqlConnection(connectionString);
    await dbConnection.OpenAsync(ct);

    // We consider the schema "initialized" only when several core tables exist.
    // If users exists but other tables are missing, we fail fast with an actionable message
    // rather than trying to re-run a non-idempotent bootstrap script.
    await using (var schemaCheck = new NpgsqlCommand("""
        SELECT
            to_regclass('public.users')::text        AS users_table,
            to_regclass('public.accounts')::text     AS accounts_table,
            to_regclass('public.categories')::text   AS categories_table,
            to_regclass('public.transactions')::text AS transactions_table,
            to_regclass('public.refresh_tokens')::text AS refresh_tokens_table
    """, dbConnection))
    {
        await using var reader = await schemaCheck.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var usersTable = reader.IsDBNull(0) ? null : reader.GetString(0);
        var accountsTable = reader.IsDBNull(1) ? null : reader.GetString(1);
        var categoriesTable = reader.IsDBNull(2) ? null : reader.GetString(2);
        var transactionsTable = reader.IsDBNull(3) ? null : reader.GetString(3);
        var refreshTokensTable = reader.IsDBNull(4) ? null : reader.GetString(4);

        var hasAnyCoreTable =
            !string.IsNullOrWhiteSpace(usersTable) ||
            !string.IsNullOrWhiteSpace(accountsTable) ||
            !string.IsNullOrWhiteSpace(categoriesTable) ||
            !string.IsNullOrWhiteSpace(transactionsTable) ||
            !string.IsNullOrWhiteSpace(refreshTokensTable);

        var hasAllCoreTables =
            !string.IsNullOrWhiteSpace(usersTable) &&
            !string.IsNullOrWhiteSpace(accountsTable) &&
            !string.IsNullOrWhiteSpace(categoriesTable) &&
            !string.IsNullOrWhiteSpace(transactionsTable) &&
            !string.IsNullOrWhiteSpace(refreshTokensTable);

        if (hasAllCoreTables)
        {
            return;
        }

        if (hasAnyCoreTable)
        {
            throw new InvalidOperationException(
                $"Database '{databaseName}' exists but is only partially initialized. " +
                "Drop the database and re-run the API to bootstrap it from Postgre.sql.");
        }
    }

    var candidatePaths = new[]
    {
        Path.Combine(environment.ContentRootPath, "DataBase", "Postgre.sql"),
        Path.Combine(environment.ContentRootPath, "Postgre.sql"),
        Path.Combine(AppContext.BaseDirectory, "DataBase", "Postgre.sql"),
        Path.Combine(AppContext.BaseDirectory, "Postgre.sql")
    };

    var scriptPath = candidatePaths.FirstOrDefault(File.Exists);
    if (scriptPath is null)
    {
        throw new FileNotFoundException("Postgre.sql was not found. Ensure it exists at KeepWalletAPI/DataBase/Postgre.sql and is copied to the output directory.");
    }

    var script = await File.ReadAllTextAsync(scriptPath, ct);
    await using var scriptCommand = new NpgsqlCommand(script, dbConnection)
    {
        CommandTimeout = 0
    };
    await scriptCommand.ExecuteNonQueryAsync(ct);
}

static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

static void AppendAuthCookies(
    HttpContext context,
    string accessCookieName,
    string refreshCookieName,
    string accessToken,
    DateTimeOffset accessTokenExpiresAt,
    string refreshToken,
    DateTimeOffset refreshTokenExpiresAt,
    bool secureCookies,
    SameSiteMode sameSiteMode,
    string refreshCookiePath)
{
    context.Response.Cookies.Append(accessCookieName, accessToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secureCookies,
        SameSite = sameSiteMode,
        Expires = accessTokenExpiresAt
    });

    context.Response.Cookies.Append(refreshCookieName, refreshToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secureCookies,
        SameSite = sameSiteMode,
        Expires = refreshTokenExpiresAt,
        Path = refreshCookiePath
    });
}

static void ClearAuthCookies(
    HttpContext context,
    string accessCookieName,
    string refreshCookieName,
    bool secureCookies,
    SameSiteMode sameSiteMode,
    string refreshCookiePath)
{
    context.Response.Cookies.Delete(accessCookieName, new CookieOptions
    {
        Secure = secureCookies,
        SameSite = sameSiteMode
    });

    context.Response.Cookies.Delete(refreshCookieName, new CookieOptions
    {
        Secure = secureCookies,
        SameSite = sameSiteMode,
        Path = refreshCookiePath
    });
}
