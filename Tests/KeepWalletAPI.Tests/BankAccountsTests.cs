using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class BankAccountsTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Рахунки: POST створює рахунок, GET повертає його")]
    public async Task Post_and_get_account()
    {
        var user = await NewUserAsync();

        using var response = await user.Client.PostAsJsonAsync(
            "/api/bank-accounts",
            new CreateBankAccountRequest("  Mono  ", "usd", 1234.56m, true));
        var account = await ApiAssert.ReadAsync<BankAccountResponse>(response, HttpStatusCode.Created);

        Assert.Equal($"/api/bank-accounts/{account.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Mono", account.Name);
        Assert.Equal("USD", account.Currency);
        Assert.Equal(1234.56m, account.Balance);
        Assert.True(account.IsDefault);
        Assert.Equal(user.Id, account.UserId);

        var single = await GetAccountAsync(user, account.Id);
        Assert.Equal(account with { GroupOwnerDisplay = user.Username }, single with { GroupOwnerDisplay = user.Username });

        var list = await GetAsync<List<BankAccountResponse>>(user.Client, "/api/bank-accounts");
        Assert.Equal([account.Id], list.Select(a => a.Id));
    }

    [Fact(DisplayName = "Рахунки: новий рахунок за замовчуванням знімає позначку з попереднього")]
    public async Task Only_one_default_account()
    {
        var user = await NewUserAsync();
        var first = await CreateAccountAsync(user, isDefault: true);
        var second = await CreateAccountAsync(user, isDefault: true);

        Assert.False((await GetAccountAsync(user, first.Id)).IsDefault);
        Assert.True((await GetAccountAsync(user, second.Id)).IsDefault);

        var list = await GetAsync<List<BankAccountResponse>>(user.Client, "/api/bank-accounts");
        Assert.Equal(second.Id, list[0].Id);
    }

    [Fact(DisplayName = "Рахунки: PUT оновлює назву, валюту, баланс і позначку за замовчуванням")]
    public async Task Put_account()
    {
        var user = await NewUserAsync();
        var defaultAccount = await CreateAccountAsync(user, isDefault: true);
        var account = await CreateAccountAsync(user);

        using var response = await user.Client.PutAsJsonAsync(
            $"/api/bank-accounts/{account.Id}",
            new UpdateBankAccountRequest("Готівка", "eur", 50m, true));
        var updated = await ApiAssert.ReadAsync<BankAccountResponse>(response);

        Assert.Equal("Готівка", updated.Name);
        Assert.Equal("EUR", updated.Currency);
        Assert.Equal(50m, updated.Balance);
        Assert.True(updated.IsDefault);
        Assert.Equal(updated.Name, (await GetAccountAsync(user, account.Id)).Name);
        Assert.False((await GetAccountAsync(user, defaultAccount.Id)).IsDefault);
    }

    [Fact(DisplayName = "Рахунки: DELETE видаляє рахунок")]
    public async Task Delete_account()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);

        using var response = await user.Client.DeleteAsync($"/api/bank-accounts/{account.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.GetAsync($"/api/bank-accounts/{account.Id}")));
        Assert.Empty(await GetAsync<List<BankAccountResponse>>(user.Client, "/api/bank-accounts"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.DeleteAsync($"/api/bank-accounts/{account.Id}")));
    }

    [Fact(DisplayName = "Рахунки: DELETE рахунку з транзакціями")]
    public async Task Delete_account_with_transactions()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var transaction = await CreateTransactionAsync(user, account.Id, await CategoryIdAsync(ExpenseCategoryName), 10m);

        using var response = await user.Client.DeleteAsync($"/api/bank-accounts/{account.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.GetAsync($"/api/transactions/{transaction.Id}")));
    }

    [Fact(DisplayName = "Рахунки: чужий рахунок недоступний для GET, PUT і DELETE")]
    public async Task Foreign_account_not_found()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var account = await CreateAccountAsync(owner, balance: 100m);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.GetAsync($"/api/bank-accounts/{account.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.PutAsJsonAsync(
            $"/api/bank-accounts/{account.Id}", new UpdateBankAccountRequest("Hacked", "UAH", 0m, false))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.DeleteAsync($"/api/bank-accounts/{account.Id}")));
        Assert.Empty(await GetAsync<List<BankAccountResponse>>(stranger.Client, "/api/bank-accounts"));

        var unchanged = await GetAccountAsync(owner, account.Id);
        Assert.Equal(account.Name, unchanged.Name);
        Assert.Equal(100m, unchanged.Balance);
    }

    [Fact(DisplayName = "Рахунки: без токена — 401")]
    public async Task Accounts_require_auth()
    {
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/bank-accounts")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.PostAsJsonAsync(
            "/api/bank-accounts", new CreateBankAccountRequest("x", "UAH", 0m, false))));
    }

    [Fact(DisplayName = "Рахунки: спільний доступ через групу і його скасування")]
    public async Task Share_account_with_group()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        var account = await CreateAccountAsync(owner);

        using var share = await owner.Client.PutAsJsonAsync($"/api/bank-accounts/{account.Id}/group", new ShareResourceWithGroupRequest(group.Id));
        await ApiAssert.StatusAsync(share, HttpStatusCode.OK);

        var shared = await GetAsync<List<BankAccountResponse>>(member.Client, "/api/bank-accounts");
        var sharedAccount = Assert.Single(shared);
        Assert.Equal(account.Id, sharedAccount.Id);
        Assert.Equal(group.Id, sharedAccount.GroupId);
        Assert.Equal(group.Name, sharedAccount.GroupName);
        Assert.Equal(owner.Username, sharedAccount.GroupOwnerDisplay);

        using var memberUpdate = await member.Client.PutAsJsonAsync(
            $"/api/bank-accounts/{account.Id}", new UpdateBankAccountRequest("Сімейний", "UAH", 10m, true));
        await ApiAssert.StatusAsync(memberUpdate, HttpStatusCode.OK);
        var afterMemberUpdate = await GetAccountAsync(owner, account.Id);
        Assert.Equal("Сімейний", afterMemberUpdate.Name);
        Assert.False(afterMemberUpdate.IsDefault);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(member.Client.DeleteAsync($"/api/bank-accounts/{account.Id}")));

        using var unshare = await owner.Client.PutAsJsonAsync($"/api/bank-accounts/{account.Id}/groups", new ReplaceResourceGroupsRequest([]));
        await ApiAssert.StatusAsync(unshare, HttpStatusCode.OK);
        Assert.Empty(await GetAsync<List<BankAccountResponse>>(member.Client, "/api/bank-accounts"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(member.Client.GetAsync($"/api/bank-accounts/{account.Id}")));
    }

    [Fact(DisplayName = "Рахунки: не можна поділитися з чужою групою")]
    public async Task Share_account_with_foreign_group()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var foreignGroup = await CreateGroupAsync(stranger);
        var account = await CreateAccountAsync(owner);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(owner.Client.PutAsJsonAsync(
            $"/api/bank-accounts/{account.Id}/group", new ShareResourceWithGroupRequest(foreignGroup.Id))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(owner.Client.PutAsJsonAsync(
            $"/api/bank-accounts/{account.Id}/groups", new ReplaceResourceGroupsRequest([foreignGroup.Id]))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(owner.Client.PostAsJsonAsync(
            "/api/bank-accounts", new CreateBankAccountRequest("x", "UAH", 0m, false, foreignGroup.Id))));
    }
}
