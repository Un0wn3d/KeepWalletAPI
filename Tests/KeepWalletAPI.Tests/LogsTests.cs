using System.Net;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class LogsTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Журнал: дії користувача записуються в журнал")]
    public async Task Logs_record_user_actions()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        await CreateTransactionAsync(user, account.Id, await CategoryIdAsync(ExpenseCategoryName), 10m);

        var logs = await GetAsync<List<AuditLogResponse>>(user.Client, "/api/logs/me");

        Assert.Contains(logs, l => l.Action == "CREATE_USERS");
        Assert.Contains(logs, l => l.Action == "CREATE_ACCOUNTS" && l.Details!.Contains(account.Id.ToString()));
        Assert.Contains(logs, l => l.Action == "CREATE_TRANSACTIONS");
        Assert.All(logs, l => Assert.Equal(user.Id, l.UserId));
        Assert.All(logs, l => Assert.Equal(user.Username, l.Username));
        Assert.Equal(logs.OrderByDescending(l => l.CreatedAt).Select(l => l.Id), logs.Select(l => l.Id));
    }

    [Fact(DisplayName = "Журнал: користувач бачить дії учасників своїх груп, але не сторонніх")]
    public async Task Logs_visibility()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var stranger = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        await CreateAccountAsync(stranger);

        var logs = await GetAsync<List<AuditLogResponse>>(owner.Client, "/api/logs/me");

        Assert.Contains(logs, l => l.UserId == member.Id);
        Assert.DoesNotContain(logs, l => l.UserId == stranger.Id);
    }

    [Fact(DisplayName = "Журнал: адміністратор бачить дії всіх користувачів")]
    public async Task Admin_sees_all_logs()
    {
        var stranger = await NewUserAsync();
        await CreateAccountAsync(stranger);

        var logs = await GetAsync<List<AuditLogResponse>>(Admin.Client, "/api/logs/me");

        Assert.Contains(logs, l => l.UserId == stranger.Id && l.Action == "CREATE_ACCOUNTS");
    }

    [Fact(DisplayName = "Журнал: без токена — 401")]
    public async Task Logs_require_auth()
    {
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/logs/me")));
    }
}
