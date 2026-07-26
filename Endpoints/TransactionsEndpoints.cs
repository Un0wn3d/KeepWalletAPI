using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class TransactionsEndpoints
{
    internal static void MapTransactionsEndpoints(this WebApplication app)
    {
app.MapGet("/api/transactions", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownTransactions = await db.Transactions
        .AsNoTracking()
        .Where(t => t.RecurringPaymentId == null && t.Account != null && t.Account.UserId == userId.Value)
        .OrderByDescending(t => t.TransactionDate)
        .Select(t => new TransactionResponse(
            t.Id,
            t.AccountId,
            null,
            null,
            t.Account != null && t.Account.User != null && t.Account.User.IsActive ? t.Account.User.Username : null,
            t.CategoryId,
            t.SavingId,
            t.RecurringPaymentId,
            t.Amount,
            t.Description,
            t.TransactionDate))
        .ToListAsync(ct);

    var sharedTransactions = await db.GroupResourceAccess
        .AsNoTracking()
        .Where(access => access.TransactionId != null &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
        .Join(db.Transactions,
            access => access.TransactionId!.Value,
            transaction => transaction.Id,
            (access, transaction) => new { access, transaction })
        .Join(db.Groups,
            x => x.access.GroupId,
            group => group.Id,
            (x, group) => new { x.access, x.transaction, group })
        .Where(x => x.transaction.RecurringPaymentId == null)
        .OrderByDescending(x => x.transaction.TransactionDate)
        .Select(x => new TransactionResponse(
            x.transaction.Id,
            x.transaction.AccountId,
            x.access.GroupId,
            x.group.Name,
            x.transaction.Account != null && x.transaction.Account.User != null && x.transaction.Account.User.IsActive ? x.transaction.Account.User.Username : null,
            x.transaction.CategoryId,
            x.transaction.SavingId,
            x.transaction.RecurringPaymentId,
            x.transaction.Amount,
            x.transaction.Description,
            x.transaction.TransactionDate))
        .ToListAsync(ct);

    var sharedSavingTransactions = await db.GroupResourceAccess
        .AsNoTracking()
        .Where(access => access.SavingId != null &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
        .Join(db.Transactions,
            access => access.SavingId!.Value,
            transaction => transaction.SavingId,
            (access, transaction) => new { access, transaction })
        .Join(db.Groups,
            x => x.access.GroupId,
            group => group.Id,
            (x, group) => new { x.access, x.transaction, group })
        .Where(x => x.transaction.RecurringPaymentId == null)
        .OrderByDescending(x => x.transaction.TransactionDate)
        .Select(x => new TransactionResponse(
            x.transaction.Id,
            x.transaction.AccountId,
            x.access.GroupId,
            x.group.Name,
            x.transaction.Account != null && x.transaction.Account.User != null && x.transaction.Account.User.IsActive ? x.transaction.Account.User.Username : null,
            x.transaction.CategoryId,
            x.transaction.SavingId,
            x.transaction.RecurringPaymentId,
            x.transaction.Amount,
            x.transaction.Description,
            x.transaction.TransactionDate))
        .ToListAsync(ct);

    var transactions = ownTransactions
        .Concat(sharedTransactions)
        .Concat(sharedSavingTransactions)
        .GroupBy(t => t.Id)
        .Select(g => g.First())
        .OrderByDescending(t => t.TransactionDate)
        .ToList();
    return Results.Ok(transactions);
}).RequireAuthorization();

app.MapGet("/api/transactions/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .AsNoTracking()
        .Where(t => t.Id == id && t.Account != null && (t.Account.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.TransactionId == t.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value)) ||
            db.GroupResourceAccess.Any(access => access.AccountId == t.AccountId &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))))
        .Select(t => new TransactionResponse(
            t.Id,
            t.AccountId,
            db.GroupResourceAccess
                .Where(access => access.TransactionId == t.Id &&
                    db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
                .Select(access => (Guid?)access.GroupId)
                .FirstOrDefault(),
            null,
            t.Account != null && t.Account.User != null && t.Account.User.IsActive ? t.Account.User.Username : null,
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
        db.GroupResourceAccess.Any(access => access.AccountId == a.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transactionAccount is null) return Results.BadRequest(new { message = "Account does not exist." });

    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    if (request.SavingId.HasValue)
    {
        var canUseSaving = await CanUseSavingAsync(db, request.SavingId.Value, userId.Value, requireManage: true, ct);
        if (!canUseSaving) return Results.BadRequest(new { message = "Saving does not exist." });
    }

    var transaction = new Transaction
    {
        AccountId = request.AccountId,
        CategoryId = request.CategoryId,
        SavingId = request.SavingId,
        RecurringPaymentId = request.RecurringPaymentId,
        Amount = request.Amount,
        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
        TransactionDate = request.TransactionDate.ToUniversalTime()
    };

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync(ct);
    if (request.GroupId.HasValue)
    {
        await SetTransactionGroupAccessAsync(db, transaction.Id, request.GroupId.Value, userId.Value, ct);
    }
    return Results.Created($"/api/transactions/{transaction.Id}", ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapPut("/api/transactions/{id:int}", async (int id, UpdateTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.Account != null && (t.Account.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.TransactionId == t.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer)) ||
            db.GroupResourceAccess.Any(access => access.AccountId == t.AccountId &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();
    var ownsTransaction = transaction.Account!.UserId == userId.Value;
    if (!ownsTransaction && transaction.AccountId != request.AccountId)
    {
        return Results.BadRequest(new { message = "Only the owner can move this transaction to another account." });
    }

    var targetAccount = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        db.GroupResourceAccess.Any(access => access.AccountId == a.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (targetAccount is null) return Results.BadRequest(new { message = "Account does not exist." });

    if (ownsTransaction && request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
        if (!canShare) return Results.NotFound();
    }

    if (request.SavingId.HasValue)
    {
        var canUseSaving = await CanUseSavingAsync(db, request.SavingId.Value, userId.Value, requireManage: true, ct);
        if (!canUseSaving) return Results.BadRequest(new { message = "Saving does not exist." });
    }

    transaction.AccountId = request.AccountId;
    transaction.CategoryId = request.CategoryId;
    transaction.SavingId = request.SavingId;
    transaction.RecurringPaymentId = request.RecurringPaymentId;
    transaction.Amount = request.Amount;
    transaction.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
    transaction.TransactionDate = request.TransactionDate.ToUniversalTime();
    await db.SaveChangesAsync(ct);
    if (ownsTransaction && request.GroupId.HasValue)
    {
        await SetTransactionGroupAccessAsync(db, transaction.Id, request.GroupId.Value, userId.Value, ct);
    }
    else if (ownsTransaction)
    {
        await ReplaceTransactionGroupAccessAsync(db, transaction.Id, null, userId.Value, ct);
    }
    return Results.Ok(ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapPut("/api/transactions/{id:int}/groups", async (int id, ReplaceResourceGroupsRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.Account != null && t.Account.UserId == userId.Value, ct);
    if (transaction is null) return Results.NotFound();

    var groupIds = request.GroupIds.Distinct().ToArray();
    var allowedGroupCount = await db.GroupMembers
        .CountAsync(m => groupIds.Contains(m.GroupId) && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer, ct);
    if (allowedGroupCount != groupIds.Length) return Results.NotFound();

    await ReplaceTransactionGroupAccessesAsync(db, transaction.Id, groupIds, userId.Value, ct);
    return Results.Ok(ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapDelete("/api/transactions/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.Account != null && (t.Account.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.TransactionId == t.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (transaction is null) return Results.NotFound();

    db.Transactions.Remove(transaction);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

    }
}
