using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class PlannedTransactionsTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static readonly TimeSpan Monthly = TimeSpan.FromDays(30);
    private static readonly DateOnly DueDate = new(2026, 11, 15);

    private async Task<TransactionResponse> CreatePlannedAsync(
        ApiUser user,
        Guid accountId,
        decimal amount = 400m,
        TimeSpan? repeatInterval = null,
        Guid? groupId = null)
    {
        using var response = await user.Client.PostAsJsonAsync(
            "/api/planned-transactions",
            new CreatePlannedTransactionRequest(
                accountId, groupId, await CategoryIdAsync(ExpenseCategoryName), "Інтернет", amount, null, DueDate, repeatInterval ?? Monthly));
        return await ApiAssert.ReadAsync<TransactionResponse>(response, HttpStatusCode.Created);
    }

    [Fact(DisplayName = "Заплановані: POST створює платіж і не змінює баланс")]
    public async Task Post_planned_transaction()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);

        var created = await CreatePlannedAsync(user, account.Id);

        Assert.NotNull(created.RecurringPaymentId);
        Assert.Equal("Інтернет", created.Description);
        Assert.Equal(1000m, (await GetAccountAsync(user, account.Id)).Balance);

        var planned = Assert.Single(await GetAsync<List<PlannedTransactionResponse>>(user.Client, "/api/planned-transactions"));
        Assert.Equal(created.Id, planned.Id);
        Assert.Equal("Інтернет", planned.Name);
        Assert.Equal(400m, planned.Amount);
        Assert.Equal(DueDate, planned.NextDueDate);
        Assert.Equal(Monthly, planned.RepeatInterval);
        Assert.True(planned.IsActive);
        Assert.Equal("UAH", planned.Currency);
        Assert.Equal(user.Username, planned.OwnerDisplay);

        Assert.Empty(await GetAsync<List<TransactionResponse>>(user.Client, "/api/transactions"));
    }

    [Fact(DisplayName = "Заплановані: POST з неіснуючим рахунком або категорією — 400")]
    public async Task Post_planned_transaction_invalid()
    {
        var user = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var foreignAccount = await CreateAccountAsync(stranger);
        var category = await CategoryIdAsync(ExpenseCategoryName);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(user.Client.PostAsJsonAsync(
            "/api/planned-transactions",
            new CreatePlannedTransactionRequest(foreignAccount.Id, null, category, "x", 1m, null, DueDate, Monthly))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(user.Client.PostAsJsonAsync(
            "/api/planned-transactions",
            new CreatePlannedTransactionRequest(account.Id, null, 999999, "x", 1m, null, DueDate, Monthly))));
        Assert.Empty(await GetAsync<List<PlannedTransactionResponse>>(user.Client, "/api/planned-transactions"));
    }

    [Fact(DisplayName = "Заплановані: PUT змінює назву, суму, дату і період")]
    public async Task Put_planned_transaction()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);
        var created = await CreatePlannedAsync(user, account.Id);
        var newDate = new DateOnly(2026, 12, 1);

        using var response = await user.Client.PutAsJsonAsync(
            $"/api/planned-transactions/{created.Id}",
            new CreatePlannedTransactionRequest(
                account.Id, null, await CategoryIdAsync(ExpenseCategoryName), "Мобільний", 150m, "Щотижня", newDate, TimeSpan.FromDays(7)));
        var updated = await ApiAssert.ReadAsync<TransactionResponse>(response);
        Assert.Equal(150m, updated.Amount);

        var planned = Assert.Single(await GetAsync<List<PlannedTransactionResponse>>(user.Client, "/api/planned-transactions"));
        Assert.Equal("Мобільний", planned.Name);
        Assert.Equal("Щотижня", planned.Description);
        Assert.Equal(150m, planned.Amount);
        Assert.Equal(newDate, planned.NextDueDate);
        Assert.Equal(TimeSpan.FromDays(7), planned.RepeatInterval);
        Assert.Equal(1000m, (await GetAccountAsync(user, account.Id)).Balance);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.PutAsJsonAsync(
            "/api/planned-transactions/999999",
            new CreatePlannedTransactionRequest(account.Id, null, 1, "x", 1m, null, newDate, Monthly))));
    }

    [Fact(DisplayName = "Заплановані: підтвердження повторюваного платежу списує гроші і переносить дату")]
    public async Task Confirm_recurring_payment()
    {
        var user = await NewUserAsync();
        var plannedAccount = await CreateAccountAsync(user, balance: 1000m);
        var payAccount = await CreateAccountAsync(user, balance: 2000m);
        var planned = await CreatePlannedAsync(user, plannedAccount.Id, amount: 400m);

        using var response = await user.Client.PostAsJsonAsync(
            $"/api/planned-transactions/{planned.Id}/confirm", new ConfirmPlannedTransactionRequest(payAccount.Id));
        var paid = await ApiAssert.ReadAsync<TransactionResponse>(response);

        Assert.NotEqual(planned.Id, paid.Id);
        Assert.Null(paid.RecurringPaymentId);
        Assert.Equal(payAccount.Id, paid.AccountId);
        Assert.Equal(400m, paid.Amount);
        Assert.Equal(1600m, (await GetAccountAsync(user, payAccount.Id)).Balance);
        Assert.Equal(1000m, (await GetAccountAsync(user, plannedAccount.Id)).Balance);

        var stillPlanned = Assert.Single(await GetAsync<List<PlannedTransactionResponse>>(user.Client, "/api/planned-transactions"));
        Assert.Equal(DueDate.AddMonths(1), stillPlanned.NextDueDate);
        Assert.True(stillPlanned.IsActive);

        var transactions = await GetAsync<List<TransactionResponse>>(user.Client, "/api/transactions");
        Assert.Equal([paid.Id], transactions.Select(t => t.Id));
    }

    [Fact(DisplayName = "Заплановані: підтвердження разового платежу перетворює його на звичайну транзакцію")]
    public async Task Confirm_one_time_payment()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);
        var planned = await CreatePlannedAsync(user, account.Id, amount: 250m, repeatInterval: TimeSpan.Zero);

        using var response = await user.Client.PostAsJsonAsync(
            $"/api/planned-transactions/{planned.Id}/confirm", new ConfirmPlannedTransactionRequest(account.Id));
        var paid = await ApiAssert.ReadAsync<TransactionResponse>(response);

        Assert.Equal(planned.Id, paid.Id);
        Assert.Null(paid.RecurringPaymentId);
        Assert.Equal(750m, (await GetAccountAsync(user, account.Id)).Balance);
        Assert.Empty(await GetAsync<List<PlannedTransactionResponse>>(user.Client, "/api/planned-transactions"));
        Assert.Contains(await GetAsync<List<TransactionResponse>>(user.Client, "/api/transactions"), t => t.Id == planned.Id);
    }

    [Fact(DisplayName = "Заплановані: підтвердження на чужий рахунок — 404")]
    public async Task Confirm_to_foreign_account()
    {
        var user = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var foreignAccount = await CreateAccountAsync(stranger, balance: 500m);
        var planned = await CreatePlannedAsync(user, account.Id);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.PostAsJsonAsync(
            $"/api/planned-transactions/{planned.Id}/confirm", new ConfirmPlannedTransactionRequest(foreignAccount.Id))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.PostAsJsonAsync(
            $"/api/planned-transactions/{planned.Id}/confirm", new ConfirmPlannedTransactionRequest(foreignAccount.Id))));
        Assert.Equal(500m, (await GetAccountAsync(stranger, foreignAccount.Id)).Balance);
    }

    [Fact(DisplayName = "Заплановані: DELETE видаляє платіж")]
    public async Task Delete_planned_transaction()
    {
        var user = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);
        var planned = await CreatePlannedAsync(user, account.Id);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.DeleteAsync($"/api/planned-transactions/{planned.Id}")));

        using var response = await user.Client.DeleteAsync($"/api/planned-transactions/{planned.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Empty(await GetAsync<List<PlannedTransactionResponse>>(user.Client, "/api/planned-transactions"));
        Assert.Equal(1000m, (await GetAccountAsync(user, account.Id)).Balance);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.DeleteAsync($"/api/planned-transactions/{planned.Id}")));
    }

    [Fact(DisplayName = "Заплановані: спільний платіж видно учасникам групи")]
    public async Task Shared_planned_transaction()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var stranger = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        var account = await CreateAccountAsync(owner);

        var planned = await CreatePlannedAsync(owner, account.Id, groupId: group.Id);

        var shared = Assert.Single(await GetAsync<List<PlannedTransactionResponse>>(member.Client, "/api/planned-transactions"));
        Assert.Equal(planned.Id, shared.Id);
        Assert.Equal(group.Id, shared.GroupId);
        Assert.Equal(group.Name, shared.GroupName);
        Assert.Empty(await GetAsync<List<PlannedTransactionResponse>>(stranger.Client, "/api/planned-transactions"));
    }
}
