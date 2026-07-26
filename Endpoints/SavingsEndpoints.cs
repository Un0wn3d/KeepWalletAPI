using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class SavingsEndpoints
{
    internal static void MapSavingsEndpoints(this WebApplication app)
    {
app.MapGet("/api/savings", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownSavings = await db.Savings
        .AsNoTracking()
        .Where(s => s.UserId == userId.Value)
        .OrderBy(s => s.IsCompleted)
        .ThenBy(s => s.Deadline)
        .ThenBy(s => s.Name)
        .Select(s => new SavingResponse(
            s.Id,
            s.UserId,
            null,
            s.Name,
            s.TargetAmount,
            s.CurrentAmount,
            s.Deadline,
            s.Currency,
            s.IconKey,
            s.Color,
            s.IsCompleted,
            db.Users
                .Where(u => u.Id == s.UserId)
                .Select(u => u.Username)
                .FirstOrDefault()))
        .ToListAsync(ct);

    var sharedSavings = await db.GroupResourceAccess
        .AsNoTracking()
        .Where(access => access.SavingId != null &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
        .Join(db.Savings,
            access => access.SavingId!.Value,
            saving => saving.Id,
            (access, saving) => new { access, saving })
        .OrderBy(x => x.saving.IsCompleted)
        .ThenBy(x => x.saving.Deadline)
        .ThenBy(x => x.saving.Name)
        .Select(x => new SavingResponse(
            x.saving.Id,
            x.saving.UserId,
            x.access.GroupId,
            x.saving.Name,
            x.saving.TargetAmount,
            x.saving.CurrentAmount,
            x.saving.Deadline,
            x.saving.Currency,
            x.saving.IconKey,
            x.saving.Color,
            x.saving.IsCompleted,
            db.Users
                .Where(u => u.Id == x.saving.UserId)
                .Select(u => u.Username)
                .FirstOrDefault()))
        .ToListAsync(ct);

    var savings = ownSavings.Concat(sharedSavings).ToList();
    return Results.Ok(savings);
}).RequireAuthorization();

app.MapGet("/api/savings/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = await db.Savings
        .AsNoTracking()
        .Where(s => s.Id == id && (s.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.SavingId == s.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))))
        .Select(s => new SavingResponse(
            s.Id,
            s.UserId,
            db.GroupResourceAccess
                .Where(access => access.SavingId == s.Id &&
                    db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
                .Select(access => (Guid?)access.GroupId)
                .FirstOrDefault(),
            s.Name,
            s.TargetAmount,
            s.CurrentAmount,
            s.Deadline,
            s.Currency,
            s.IconKey,
            s.Color,
            s.IsCompleted,
            db.Users
                .Where(u => u.Id == s.UserId)
                .Select(u => u.Username)
                .FirstOrDefault()))
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
        db.GroupResourceAccess.Any(access => access.SavingId == s.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
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

    if (request.GroupId.HasValue)
    {
        await SetSavingGroupAccessAsync(db, saving.Id, request.GroupId.Value, userId.Value, ct);
    }
    else
    {
        await ReplaceSavingGroupAccessAsync(db, saving.Id, null, userId.Value, ct);
    }
    return Results.Ok(ToSavingResponse(saving));
}).RequireAuthorization();

app.MapPut("/api/savings/{id:int}/groups", async (int id, ReplaceResourceGroupsRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var saving = await db.Savings.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId.Value, ct);
    if (saving is null) return Results.NotFound();

    var groupIds = request.GroupIds.Distinct().ToArray();
    var allowedGroupCount = await db.GroupMembers
        .CountAsync(m => groupIds.Contains(m.GroupId) && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
    if (allowedGroupCount != groupIds.Length) return Results.NotFound();

    await ReplaceSavingGroupAccessesAsync(db, saving.Id, groupIds, userId.Value, ct);
    return Results.Ok(ToSavingResponse(saving));
}).RequireAuthorization();

app.MapGet("/api/savings/{savingId:int}/items", async (int savingId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownsSaving = await CanUseSavingAsync(db, savingId, userId.Value, requireManage: false, ct);
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

    var ownsSaving = await CanUseSavingAsync(db, savingId, userId.Value, requireManage: true, ct);
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

    var ownsSaving = await CanUseSavingAsync(db, savingId, userId.Value, requireManage: true, ct);
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

    var ownsSaving = await CanUseSavingAsync(db, savingId, userId.Value, requireManage: true, ct);
    if (!ownsSaving) return Results.NotFound();

    var item = await db.SavingItems.FirstOrDefaultAsync(i => i.Id == itemId && i.SavingId == savingId, ct);
    if (item is null) return Results.NotFound();

    db.SavingItems.Remove(item);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

    }
}
