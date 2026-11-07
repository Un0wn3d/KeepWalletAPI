using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class BudgetsTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static UpsertBudgetRequest Budget(decimal amount, Guid? groupId = null) =>
        new(groupId, amount, null, new DateOnly(2026, 1, 1), true);

    [Fact(DisplayName = "Бюджети: PUT створює ліміт, повторний PUT оновлює той самий")]
    public async Task Put_budget_upsert()
    {
        var user = await NewUserAsync();
        await CreateAccountAsync(user);
        var category = await CategoryIdAsync(ExpenseCategoryName);

        using var created = await user.Client.PutAsJsonAsync($"/api/budgets/category/{category}", Budget(1500m));
        var budget = await ApiAssert.ReadAsync<BudgetResponse>(created);
        Assert.Equal(category, budget.CategoryId);
        Assert.Equal(1500m, budget.Amount);
        Assert.Equal(user.Id, budget.UserId);

        using var updated = await user.Client.PutAsJsonAsync($"/api/budgets/category/{category}", Budget(2000m));
        var updatedBudget = await ApiAssert.ReadAsync<BudgetResponse>(updated);
        Assert.Equal(budget.Id, updatedBudget.Id);
        Assert.Equal(2000m, updatedBudget.Amount);

        var budgets = await GetAsync<List<BudgetResponse>>(user.Client, "/api/budgets");
        Assert.Equal([(budget.Id, 2000m)], budgets.Select(b => (b.Id, b.Amount)));
    }

    [Fact(DisplayName = "Бюджети: користувач не бачить чужі бюджети")]
    public async Task Get_budgets_isolated()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        await CreateAccountAsync(owner);
        await ApiAssert.StatusAsync(
            await owner.Client.PutAsJsonAsync($"/api/budgets/category/{await CategoryIdAsync(ExpenseCategoryName)}", Budget(100m)),
            HttpStatusCode.OK);

        Assert.Empty(await GetAsync<List<BudgetResponse>>(stranger.Client, "/api/budgets"));
    }

    [Fact(DisplayName = "Бюджети: без рахунку або з неіснуючою категорією — 400")]
    public async Task Put_budget_invalid()
    {
        var user = await NewUserAsync();
        var category = await CategoryIdAsync(ExpenseCategoryName);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            user.Client.PutAsJsonAsync($"/api/budgets/category/{category}", Budget(100m))));

        await CreateAccountAsync(user);
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            user.Client.PutAsJsonAsync("/api/budgets/category/999999", Budget(100m))));

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/budgets")));
    }

    [Fact(DisplayName = "Бюджети: груповий бюджет видно учасникам, глядачу змінювати не можна")]
    public async Task Group_budget()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var viewer = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        await AddMemberAsync(owner, group.Id, viewer, "viewer");
        await CreateAccountAsync(owner, groupId: group.Id);
        var category = await CategoryIdAsync(ExpenseCategoryName);

        using var created = await owner.Client.PutAsJsonAsync($"/api/budgets/category/{category}", Budget(700m, group.Id));
        var budget = await ApiAssert.ReadAsync<BudgetResponse>(created);
        Assert.Equal(group.Id, budget.GroupId);

        var memberBudgets = await GetAsync<List<BudgetResponse>>(member.Client, "/api/budgets");
        Assert.Contains(memberBudgets, b => b.Id == budget.Id && b.Amount == 700m);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(
            viewer.Client.PutAsJsonAsync($"/api/budgets/category/{category}", Budget(1m, group.Id))));
    }
}
