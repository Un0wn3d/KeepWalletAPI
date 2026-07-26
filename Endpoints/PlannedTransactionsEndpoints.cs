using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class PlannedTransactionsEndpoints
{
    internal static void MapPlannedTransactionsEndpoints(this WebApplication app)
    {
app.MapGet("/api/planned-transactions", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var ownTransactionRows = await db.Transactions
        .AsNoTracking()
        .Where(t => t.RecurringPaymentId != null && t.Account != null && t.Account.UserId == userId.Value)
        .OrderBy(t => t.TransactionDate)
        .Join(db.ScheduledPayments,
            t => t.RecurringPaymentId!.Value,
            p => p.Id,
            (t, p) => new
            {
                t.Id,
                t.AccountId,
                GroupId = (Guid?)null,
                GroupName = (string?)null,
                t.CategoryId,
                RecurringPaymentId = p.Id,
                p.Name,
                t.Amount,
                t.Description,
                t.TransactionDate,
                p.NextDueDate,
                p.IsActive,
                Currency = t.Account != null ? t.Account.Currency : null,
                OwnerDisplay = t.Account != null && t.Account.User != null && t.Account.User.IsActive
                    ? t.Account.User.Username
                    : null
        })
        .ToListAsync(ct);

    var sharedTransactionRows = await db.GroupResourceAccess
        .AsNoTracking()
        .Where(access => access.TransactionId != null &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))
        .Join(db.Transactions,
            access => access.TransactionId!.Value,
            transaction => transaction.Id,
            (access, transaction) => new { access, transaction })
        .Where(x => x.transaction.RecurringPaymentId != null)
        .Join(db.ScheduledPayments,
            x => x.transaction.RecurringPaymentId!.Value,
            payment => payment.Id,
            (x, payment) => new
            {
                x.transaction.Id,
                x.transaction.AccountId,
                GroupId = (Guid?)x.access.GroupId,
                GroupName = x.access.Group != null ? x.access.Group.Name : null,
                x.transaction.CategoryId,
                RecurringPaymentId = payment.Id,
                payment.Name,
                x.transaction.Amount,
                x.transaction.Description,
                x.transaction.TransactionDate,
                payment.NextDueDate,
                payment.IsActive,
                Currency = x.transaction.Account != null ? x.transaction.Account.Currency : null,
                OwnerDisplay = x.transaction.Account != null && x.transaction.Account.User != null && x.transaction.Account.User.IsActive
                    ? x.transaction.Account.User.Username
                    : null
            })
        .ToListAsync(ct);

    var transactions = new List<PlannedTransactionResponse>();
    foreach (var x in ownTransactionRows.Concat(sharedTransactionRows))
    {
        var repeatInterval = await GetRepeatIntervalAsync(db, x.RecurringPaymentId, ct);
        transactions.Add(new PlannedTransactionResponse(
            x.Id,
            x.AccountId,
            x.GroupId,
            x.GroupName,
            x.CategoryId,
            x.RecurringPaymentId,
            x.Name,
            x.Amount,
            x.Description,
            x.TransactionDate,
            repeatInterval,
            ToDateOnly(x.NextDueDate),
            x.IsActive,
            x.OwnerDisplay,
            x.Currency));
    }

    return Results.Ok(transactions);
}).RequireAuthorization();

app.MapPost("/api/planned-transactions", async (CreatePlannedTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        db.GroupResourceAccess.Any(access => access.AccountId == a.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    var ownsAccount = account is not null;
    if (!ownsAccount) return Results.BadRequest(new { message = "Account does not exist." });

    var hasCategory = await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct);
    if (!hasCategory) return Results.BadRequest(new { message = "Category does not exist." });
    if (request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(
            m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer,
            ct);
        if (!canShare) return Results.NotFound();
    }

    var paymentId = await InsertScheduledPaymentAsync(db, request.Name.Trim(), request.RepeatInterval, ToUtcDateTimeOffset(request.NextDueDate), ct);

    var transaction = new Transaction
    {
        AccountId = request.AccountId,
        CategoryId = request.CategoryId,
        RecurringPaymentId = paymentId,
        Amount = request.Amount,
        Description = string.IsNullOrWhiteSpace(request.Description) ? request.Name.Trim() : request.Description.Trim(),
        TransactionDate = new DateTimeOffset(request.NextDueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
    };

    db.Transactions.Add(transaction);
    await db.SaveChangesAsync(ct);
    if (request.GroupId.HasValue)
    {
        await SetTransactionGroupAccessAsync(db, transaction.Id, request.GroupId.Value, userId.Value, ct);
    }
    return Results.Created($"/api/planned-transactions/{transaction.Id}", ToTransactionResponse(transaction));
}).RequireAuthorization();

app.MapPut("/api/planned-transactions/{id:int}", async (int id, CreatePlannedTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
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

    var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && (a.UserId == userId.Value ||
        db.GroupResourceAccess.Any(access => access.AccountId == a.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
    if (account is null) return Results.BadRequest(new { message = "Account does not exist." });

    var hasCategory = await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct);
    if (!hasCategory) return Results.BadRequest(new { message = "Category does not exist." });
    if (ownsTransaction && request.GroupId.HasValue)
    {
        var canShare = await db.GroupMembers.AnyAsync(
            m => m.GroupId == request.GroupId.Value && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer,
            ct);
        if (!canShare) return Results.NotFound();
    }

    var recurringPaymentId = transaction.RecurringPaymentId!.Value;
    var payment = await db.ScheduledPayments.FirstOrDefaultAsync(p => p.Id == recurringPaymentId, ct);
    if (payment is null) return Results.NotFound();

    payment.Name = request.Name.Trim();
    payment.NextDueDate = ToUtcDateTimeOffset(request.NextDueDate);
    payment.IsActive = true;
    await UpdateScheduledPaymentIntervalAsync(db, payment.Id, request.RepeatInterval, ct);

    transaction.AccountId = request.AccountId;
    transaction.CategoryId = request.CategoryId;
    transaction.Amount = request.Amount;
    transaction.Description = string.IsNullOrWhiteSpace(request.Description) ? request.Name.Trim() : request.Description.Trim();
    transaction.TransactionDate = ToUtcDateTimeOffset(request.NextDueDate);

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

app.MapPost("/api/planned-transactions/{id:int}/confirm", async (int id, ConfirmPlannedTransactionRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.TransactionId == t.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value)) ||
            db.GroupResourceAccess.Any(access => access.AccountId == t.AccountId &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value))), ct);
    if (transaction is null) return Results.NotFound();

    var targetAccount = await db.BankAccounts
        .FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == userId.Value, ct);
    if (targetAccount is null) return Results.NotFound();

    var recurringPaymentId = transaction.RecurringPaymentId!.Value;
    var payment = await db.ScheduledPayments.FirstOrDefaultAsync(p => p.Id == recurringPaymentId, ct);
    if (payment is null || !payment.IsActive)
    {
        transaction.RecurringPaymentId = null;
        transaction.TransactionDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToTransactionResponse(transaction));
    }

    var repeatInterval = await GetRepeatIntervalAsync(db, payment.Id, ct);
    if (repeatInterval > TimeSpan.Zero)
    {
        var nextDueDate = AddRepeatInterval(ToDateOnly(payment.NextDueDate), repeatInterval);
        payment.NextDueDate = ToUtcDateTimeOffset(nextDueDate);

        var paidTransaction = new Transaction
        {
            AccountId = targetAccount.Id,
            CategoryId = transaction.CategoryId,
            Amount = transaction.Amount,
            Description = transaction.Description,
            TransactionDate = DateTimeOffset.UtcNow
        };
        db.Transactions.Add(paidTransaction);

        transaction.TransactionDate = ToUtcDateTimeOffset(nextDueDate);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToTransactionResponse(paidTransaction));
    }
    else
    {
        payment.IsActive = false;

        transaction.RecurringPaymentId = null;
        transaction.AccountId = targetAccount.Id;
        transaction.TransactionDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToTransactionResponse(transaction));
    }
}).RequireAuthorization();

app.MapDelete("/api/planned-transactions/{id:int}", async (int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var transaction = await db.Transactions
        .Include(t => t.Account)
        .FirstOrDefaultAsync(t => t.Id == id && t.RecurringPaymentId != null && t.Account != null && (t.Account.UserId == userId.Value ||
            db.GroupResourceAccess.Any(access => access.TransactionId == t.Id &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer)) ||
            db.GroupResourceAccess.Any(access => access.AccountId == t.AccountId &&
                db.GroupMembers.Any(m => m.GroupId == access.GroupId && m.UserId == userId.Value && m.Role != UserGroupRole.Viewer))), ct);
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

    }
}
