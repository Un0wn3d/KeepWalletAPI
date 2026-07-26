using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class BudgetsEndpoints
{
    internal static void MapBudgetsEndpoints(this WebApplication app)
    {
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
            ? db.GroupResourceAccess.Any(access => access.AccountId == a.Id && access.GroupId == request.GroupId.Value)
            : a.UserId == userId.Value)
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

    }
}
