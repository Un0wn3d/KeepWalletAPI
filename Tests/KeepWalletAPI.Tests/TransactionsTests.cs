using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class TransactionsTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Транзакції: витрата зменшує баланс, дохід збільшує")]
    public async Task Post_transaction_updates_balance()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);
        var date = new DateTimeOffset(2026, 6, 5, 14, 30, 0, TimeSpan.FromHours(3));

        using var response = await user.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(account.Id, null, await CategoryIdAsync(ExpenseCategoryName), null, null, 125.50m, "  Обід  ", date));
        var expense = await ApiAssert.ReadAsync<TransactionResponse>(response, HttpStatusCode.Created);

        Assert.Equal($"/api/transactions/{expense.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Обід", expense.Description);
        Assert.Equal(date, expense.TransactionDate);
        Assert.Equal(874.50m, (await GetAccountAsync(user, account.Id)).Balance);

        await CreateTransactionAsync(user, account.Id, await CategoryIdAsync(IncomeCategoryName), 500m);
        Assert.Equal(1374.50m, (await GetAccountAsync(user, account.Id)).Balance);
    }

    [Fact(DisplayName = "Транзакції: GET списку і за id")]
    public async Task Get_transactions()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var category = await CategoryIdAsync(ExpenseCategoryName);
        var older = await CreateTransactionAsync(user, account.Id, category, 10m, "Старіша");
        await Task.Delay(20);
        var newer = await CreateTransactionAsync(user, account.Id, category, 20m, "Новіша");

        var list = await GetAsync<List<TransactionResponse>>(user.Client, "/api/transactions");
        Assert.Equal([newer.Id, older.Id], list.Select(t => t.Id));
        Assert.All(list, t => Assert.Equal(user.Username, t.SharedBy));

        var single = await GetAsync<TransactionResponse>(user.Client, $"/api/transactions/{older.Id}");
        Assert.Equal(10m, single.Amount);
        Assert.Equal("Старіша", single.Description);
        Assert.Equal(account.Id, single.AccountId);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.GetAsync("/api/transactions/999999")));
    }

    [Fact(DisplayName = "Транзакції: PUT перераховує баланс обох рахунків")]
    public async Task Put_transaction_recalculates_balance()
    {
        var user = await NewUserAsync();
        var first = await CreateAccountAsync(user, balance: 1000m);
        var second = await CreateAccountAsync(user, balance: 500m);
        var expense = await CategoryIdAsync(ExpenseCategoryName);
        var transaction = await CreateTransactionAsync(user, first.Id, expense, 100m);
        Assert.Equal(900m, (await GetAccountAsync(user, first.Id)).Balance);

        using var amountChange = await user.Client.PutAsJsonAsync(
            $"/api/transactions/{transaction.Id}",
            new UpdateTransactionRequest(first.Id, null, expense, null, null, 250m, "Більше", transaction.TransactionDate));
        var updated = await ApiAssert.ReadAsync<TransactionResponse>(amountChange);
        Assert.Equal(250m, updated.Amount);
        Assert.Equal("Більше", updated.Description);
        Assert.Equal(750m, (await GetAccountAsync(user, first.Id)).Balance);

        using var move = await user.Client.PutAsJsonAsync(
            $"/api/transactions/{transaction.Id}",
            new UpdateTransactionRequest(second.Id, null, await CategoryIdAsync(IncomeCategoryName), null, null, 40m, null, transaction.TransactionDate));
        await ApiAssert.StatusAsync(move, HttpStatusCode.OK);
        Assert.Equal(1000m, (await GetAccountAsync(user, first.Id)).Balance);
        Assert.Equal(540m, (await GetAccountAsync(user, second.Id)).Balance);
        Assert.Null((await GetAsync<TransactionResponse>(user.Client, $"/api/transactions/{transaction.Id}")).Description);
    }

    [Fact(DisplayName = "Транзакції: DELETE повертає баланс")]
    public async Task Delete_transaction_restores_balance()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);
        var transaction = await CreateTransactionAsync(user, account.Id, await CategoryIdAsync(ExpenseCategoryName), 300m);

        using var response = await user.Client.DeleteAsync($"/api/transactions/{transaction.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Equal(1000m, (await GetAccountAsync(user, account.Id)).Balance);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.GetAsync($"/api/transactions/{transaction.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.DeleteAsync($"/api/transactions/{transaction.Id}")));
    }

    [Fact(DisplayName = "Транзакції: на чужий або неіснуючий рахунок — 400")]
    public async Task Post_transaction_foreign_account()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(owner, balance: 1000m);
        var category = await CategoryIdAsync(ExpenseCategoryName);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(stranger.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(account.Id, null, category, null, null, 10m, null, DateTimeOffset.UtcNow))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(owner.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(Guid.NewGuid(), null, category, null, null, 10m, null, DateTimeOffset.UtcNow))));
        Assert.Equal(1000m, (await GetAccountAsync(owner, account.Id)).Balance);
    }

    [Fact(DisplayName = "Транзакції: неіснуюча категорія — 400, а не 500")]
    public async Task Post_transaction_unknown_category()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(user.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(account.Id, null, 999999, null, null, 10m, null, DateTimeOffset.UtcNow))));
    }

    [Fact(DisplayName = "Транзакції: чужу транзакцію не можна прочитати, змінити чи видалити")]
    public async Task Foreign_transaction_not_found()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(owner, balance: 1000m);
        var strangerAccount = await CreateAccountAsync(stranger);
        var category = await CategoryIdAsync(ExpenseCategoryName);
        var transaction = await CreateTransactionAsync(owner, account.Id, category, 100m);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.GetAsync($"/api/transactions/{transaction.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.PutAsJsonAsync(
            $"/api/transactions/{transaction.Id}",
            new UpdateTransactionRequest(strangerAccount.Id, null, category, null, null, 1m, null, DateTimeOffset.UtcNow))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.DeleteAsync($"/api/transactions/{transaction.Id}")));
        Assert.Empty(await GetAsync<List<TransactionResponse>>(stranger.Client, "/api/transactions"));

        Assert.Equal(900m, (await GetAccountAsync(owner, account.Id)).Balance);
    }

    [Fact(DisplayName = "Транзакції: транзакція у накопичення")]
    public async Task Post_transaction_with_saving()
    {
        var user = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var saving = await CreateSavingAsync(user);
        var foreignSaving = await CreateSavingAsync(stranger);
        var category = await CategoryIdAsync("Накопичення");

        var transaction = await CreateTransactionAsync(user, account.Id, category, 200m, savingId: saving.Id);
        Assert.Equal(saving.Id, transaction.SavingId);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(user.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(account.Id, null, category, foreignSaving.Id, null, 10m, null, DateTimeOffset.UtcNow))));
    }

    [Fact(DisplayName = "Транзакції: спільна транзакція видна учасникам групи")]
    public async Task Shared_transaction_visible_to_group()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        var account = await CreateAccountAsync(owner);
        var category = await CategoryIdAsync(ExpenseCategoryName);

        using var response = await owner.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(account.Id, group.Id, category, null, null, 70m, "Спільна", DateTimeOffset.UtcNow));
        var transaction = await ApiAssert.ReadAsync<TransactionResponse>(response, HttpStatusCode.Created);

        var memberList = await GetAsync<List<TransactionResponse>>(member.Client, "/api/transactions");
        var shared = Assert.Single(memberList);
        Assert.Equal(transaction.Id, shared.Id);
        Assert.Equal(group.Id, shared.GroupId);
        Assert.Equal(group.Name, shared.GroupName);
        Assert.Equal(group.Id, (await GetAsync<TransactionResponse>(member.Client, $"/api/transactions/{transaction.Id}")).GroupId);

        using var unshare = await owner.Client.PutAsJsonAsync($"/api/transactions/{transaction.Id}/groups", new ReplaceResourceGroupsRequest([]));
        await ApiAssert.StatusAsync(unshare, HttpStatusCode.OK);
        Assert.Empty(await GetAsync<List<TransactionResponse>>(member.Client, "/api/transactions"));
    }

    [Fact(DisplayName = "Транзакції: учасник може додати транзакцію на спільний рахунок, глядач — ні")]
    public async Task Shared_account_transactions_by_role()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var viewer = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        await AddMemberAsync(owner, group.Id, viewer, "viewer");
        var account = await CreateAccountAsync(owner, balance: 1000m, groupId: group.Id);
        var category = await CategoryIdAsync(ExpenseCategoryName);

        await CreateTransactionAsync(member, account.Id, category, 100m);
        Assert.Equal(900m, (await GetAccountAsync(owner, account.Id)).Balance);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(viewer.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(account.Id, null, category, null, null, 10m, null, DateTimeOffset.UtcNow))));
        Assert.Equal(900m, (await GetAccountAsync(owner, account.Id)).Balance);
    }

    [Fact(DisplayName = "Транзакції: без токена — 401")]
    public async Task Transactions_require_auth()
    {
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/transactions")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.DeleteAsync("/api/transactions/1")));
    }
}
