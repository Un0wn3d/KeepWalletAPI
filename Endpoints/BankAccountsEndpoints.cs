using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class BankAccountsEndpoints
{
    internal static void MapBankAccountsEndpoints(this WebApplication app)
    {
app.MapGet("/api/bank-accounts", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownAccounts = await db.BankAccounts
        .AsNoTracking()
        .Where(a => a.UserId == userId.Value)
        .OrderByDescending(a => a.IsDefault)
        .ThenBy(a => a.Name)
        .Select(a => new BankAccountResponse(
            a.Id,
            a.UserId,
            null,
            a.Name,
            a.Currency,
            a.Balance,
            a.IsDefault,
            null,
            null,
            db.Users
                .Where(u => u.Id == a.UserId)
                .Select(u => u.Username)
                .FirstOrDefault()))
        .ToListAsync(ct);

    var sharedAccounts = await db.GroupResourceAccess
        .AsNoTracking()
        .Where(access => access.AccountId != null &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
        .Join(db.BankAccounts,
            access => access.AccountId!.Value,
            account => account.Id,
            (access, account) => new { access, account })
        .Join(db.Groups,
            x => x.access.GroupId,
            group => group.Id,
            (x, group) => new { x.access, x.account, group })
        .OrderByDescending(x => x.account.IsDefault)
        .ThenBy(x => x.account.Name)
        .Select(x => new BankAccountResponse(
            x.account.Id,
            x.account.UserId,
            x.access.GroupId,
            x.account.Name,
            x.account.Currency,
            x.account.Balance,
            x.account.IsDefault,
            x.group.Name,
            x.group.Color,
            db.Users
                .Where(u => u.Id == x.account.UserId)
                .Select(u => u.Username)
                .FirstOrDefault()))
        .ToListAsync(ct);

    var accounts = ownAccounts.Concat(sharedAccounts).ToList();
    return Results.Ok(accounts);
}).RequireAuthorization();

app.MapGet("/api/bank-accounts/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts
        .AsNoTracking()
        .Where(a => a.Id == id && (a.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.AccountId == a.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))))
        .Select(a => new BankAccountResponse(
            a.Id,
            a.UserId,
            db.GroupResourceAccess
                .Where(access => access.AccountId == a.Id &&
                    db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
                .Select(access => (Guid?)access.GroupId)
                .FirstOrDefault(),
            a.Name,
            a.Currency,
            a.Balance,
            a.IsDefault,
            null,
            null,
            db.Users
                .Where(u => u.Id == a.UserId)
                .Select(u => u.Username)
                .FirstOrDefault()))
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
    };

    db.BankAccounts.Add(account);
    await db.SaveChangesAsync(ct);
    if (request.GroupId.HasValue)
    {
        await SetAccountGroupAccessAsync(db, account.Id, request.GroupId.Value, userId.Value, ct);
    }
    return Results.Created($"/api/bank-accounts/{account.Id}", await ToBankAccountResponseAsync(account, db, ct));
}).RequireAuthorization();

app.MapPut("/api/bank-accounts/{id:guid}", async (Guid id, UpdateBankAccountRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && (a.UserId == userId.Value ||
        db.GroupResourceAccess.Any(access => access.AccountId == a.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (account is null) return Results.NotFound();
    var ownsAccount = account.UserId == userId.Value;

    if (request.IsDefault && ownsAccount)
    {
        await ClearDefaultBankAccountsAsync(db, userId.Value, ct);
    }

    if (ownsAccount && request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    account.Name = request.Name.Trim();
    account.Currency = request.Currency.Trim().ToUpperInvariant();
    account.Balance = request.Balance;
    if (ownsAccount)
    {
        account.IsDefault = request.IsDefault;
    }

    await db.SaveChangesAsync(ct);
    if (ownsAccount && request.GroupId.HasValue)
    {
        await SetAccountGroupAccessAsync(db, account.Id, request.GroupId.Value, userId.Value, ct);
    }
    else if (ownsAccount)
    {
        await ReplaceAccountGroupAccessAsync(db, account.Id, null, userId.Value, ct);
    }
    return Results.Ok(await ToBankAccountResponseAsync(account, db, ct));
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

    if (request.GroupId.HasValue)
    {
        await SetAccountGroupAccessAsync(db, account.Id, request.GroupId.Value, userId.Value, ct);
    }
    else
    {
        await ReplaceAccountGroupAccessAsync(db, account.Id, null, userId.Value, ct);
    }
    return Results.Ok(await ToBankAccountResponseAsync(account, db, ct));
}).RequireAuthorization();

app.MapPut("/api/bank-accounts/{id:guid}/groups", async (Guid id, ReplaceResourceGroupsRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId.Value, ct);
    if (account is null) return Results.NotFound();

    var groupIds = request.GroupIds.Distinct().ToArray();
    var allowedGroupCount = await db.GroupMembers
        .CountAsync(m => groupIds.Contains(m.GroupId) && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
    if (allowedGroupCount != groupIds.Length) return Results.NotFound();

    await ReplaceAccountGroupAccessesAsync(db, account.Id, groupIds, userId.Value, ct);
    return Results.Ok(await ToBankAccountResponseAsync(account, db, ct));
}).RequireAuthorization();

    }
}
