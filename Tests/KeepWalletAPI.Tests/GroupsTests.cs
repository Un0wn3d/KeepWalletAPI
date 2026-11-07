using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class GroupsTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Групи: POST створює групу, автор стає власником")]
    public async Task Post_group()
    {
        var owner = await NewUserAsync();

        using var response = await owner.Client.PostAsJsonAsync("/api/groups", new CreateGroupRequest("  Сім'я  ", "HOME", "#3B82F6"));
        var group = await ApiAssert.ReadAsync<GroupResponse>(response, HttpStatusCode.Created);

        Assert.Equal($"/api/groups/{group.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Сім'я", group.Name);
        Assert.Equal("home", group.IconKey);
        Assert.Equal("#3B82F6", group.Color);
        Assert.Equal("owner", group.RoleName);
        Assert.Equal(1, group.MemberCount);
        Assert.Equal(owner.Username, group.OwnerDisplay);

        var listed = Assert.Single(await GetAsync<List<GroupResponse>>(owner.Client, "/api/groups"));
        Assert.Equal(group.Id, listed.Id);
        Assert.Equal("owner", listed.RoleName);

        var member = Assert.Single(await GetAsync<List<GroupMemberResponse>>(owner.Client, $"/api/groups/{group.Id}/members"));
        Assert.Equal(owner.Id, member.UserId);
        Assert.Equal("owner", member.RoleName);
    }

    [Fact(DisplayName = "Групи: POST без назви — 400")]
    public async Task Post_group_without_name()
    {
        var owner = await NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(owner.Client.PostAsJsonAsync("/api/groups", new CreateGroupRequest("   "))));
        Assert.Empty(await GetAsync<List<GroupResponse>>(owner.Client, "/api/groups"));
    }

    [Fact(DisplayName = "Групи: PATCH змінює назву, іконку і колір лише власник")]
    public async Task Patch_group()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);

        using var response = await owner.Client.PatchAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest("Друзі", "party", "#EF4444"));
        var updated = await ApiAssert.ReadAsync<GroupResponse>(response);
        Assert.Equal("Друзі", updated.Name);
        Assert.Equal("party", updated.IconKey);
        Assert.Equal("#EF4444", updated.Color);
        Assert.Equal(2, updated.MemberCount);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(
            member.Client.PatchAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest("Захоплено"))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            owner.Client.PatchAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest(" "))));

        var memberView = Assert.Single(await GetAsync<List<GroupResponse>>(member.Client, "/api/groups"));
        Assert.Equal("Друзі", memberView.Name);
        Assert.Equal("member", memberView.RoleName);
    }

    [Fact(DisplayName = "Групи: додавання учасника за логіном або email")]
    public async Task Add_members()
    {
        var owner = await NewUserAsync();
        var byUsername = await NewUserAsync();
        var byEmail = await NewUserAsync();
        var group = await CreateGroupAsync(owner);

        await AddMemberAsync(owner, group.Id, byUsername);
        await ApiAssert.StatusAsync(
            await owner.Client.PostAsJsonAsync($"/api/groups/{group.Id}/members", new AddGroupMemberRequest(byEmail.Email, "viewer")),
            HttpStatusCode.NoContent);

        var members = await GetAsync<List<GroupMemberResponse>>(byUsername.Client, $"/api/groups/{group.Id}/members");
        Assert.Equal(3, members.Count);
        Assert.Equal("member", members.Single(m => m.UserId == byUsername.Id).RoleName);
        Assert.Equal("viewer", members.Single(m => m.UserId == byEmail.Id).RoleName);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(owner.Client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/members", new AddGroupMemberRequest($"ghost_{Guid.NewGuid():N}", "member"))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(byEmail.Client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/members", new AddGroupMemberRequest(owner.Username, "member"))));
    }

    [Fact(DisplayName = "Групи: сторонній користувач не бачить групу і її учасників")]
    public async Task Foreign_group_not_found()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var group = await CreateGroupAsync(owner);

        Assert.Empty(await GetAsync<List<GroupResponse>>(stranger.Client, "/api/groups"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.GetAsync($"/api/groups/{group.Id}/members")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/members", new AddGroupMemberRequest(stranger.Username, "owner"))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.DeleteAsync($"/api/groups/{group.Id}")));
    }

    [Fact(DisplayName = "Групи: PATCH ролі учасника")]
    public async Task Patch_member_role()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);

        using var response = await owner.Client.PatchAsJsonAsync(
            $"/api/groups/{group.Id}/members/{member.Id}", new UpdateGroupMemberRoleRequest("viewer"));
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var members = await GetAsync<List<GroupMemberResponse>>(owner.Client, $"/api/groups/{group.Id}/members");
        Assert.Equal("viewer", members.Single(m => m.UserId == member.Id).RoleName);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(owner.Client.PatchAsJsonAsync(
            $"/api/groups/{group.Id}/members/{member.Id}", new UpdateGroupMemberRoleRequest("owner"))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(owner.Client.PatchAsJsonAsync(
            $"/api/groups/{group.Id}/members/{owner.Id}", new UpdateGroupMemberRoleRequest("member"))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(member.Client.PatchAsJsonAsync(
            $"/api/groups/{group.Id}/members/{member.Id}", new UpdateGroupMemberRoleRequest("member"))));
    }

    [Fact(DisplayName = "Групи: передача прав власника")]
    public async Task Transfer_owner()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(owner.Client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/transfer-owner", new TransferGroupOwnerRequest(owner.Id))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(member.Client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/transfer-owner", new TransferGroupOwnerRequest(member.Id))));

        using var response = await owner.Client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/transfer-owner", new TransferGroupOwnerRequest(member.Id));
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var members = await GetAsync<List<GroupMemberResponse>>(owner.Client, $"/api/groups/{group.Id}/members");
        Assert.Equal("member", members.Single(m => m.UserId == owner.Id).RoleName);
        Assert.Equal("owner", members.Single(m => m.UserId == member.Id).RoleName);
        Assert.Equal(member.Username, Assert.Single(await GetAsync<List<GroupResponse>>(owner.Client, "/api/groups")).OwnerDisplay);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(
            owner.Client.PatchAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest("Колишній власник"))));
        Assert.Equal(HttpStatusCode.OK, await StatusOfAsync(
            member.Client.PatchAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest("Новий власник"))));
    }

    [Fact(DisplayName = "Групи: власник видаляє учасника, і той втрачає доступ до спільного")]
    public async Task Remove_member()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        await CreateAccountAsync(owner, groupId: group.Id);
        Assert.Single(await GetAsync<List<BankAccountResponse>>(member.Client, "/api/bank-accounts"));

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(owner.Client.DeleteAsync($"/api/groups/{group.Id}/members/{owner.Id}")));

        using var response = await owner.Client.DeleteAsync($"/api/groups/{group.Id}/members/{member.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Empty(await GetAsync<List<GroupResponse>>(member.Client, "/api/groups"));
        Assert.Empty(await GetAsync<List<BankAccountResponse>>(member.Client, "/api/bank-accounts"));
        Assert.Single(await GetAsync<List<GroupMemberResponse>>(owner.Client, $"/api/groups/{group.Id}/members"));
    }

    [Fact(DisplayName = "Групи: учасник може вийти сам, його ресурси перестають бути спільними")]
    public async Task Member_leaves_group()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        var memberAccount = await CreateAccountAsync(member, groupId: group.Id);
        Assert.Contains(await GetAsync<List<BankAccountResponse>>(owner.Client, "/api/bank-accounts"), a => a.Id == memberAccount.Id);

        using var response = await member.Client.DeleteAsync($"/api/groups/{group.Id}/members/{member.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Empty(await GetAsync<List<GroupResponse>>(member.Client, "/api/groups"));
        Assert.DoesNotContain(await GetAsync<List<BankAccountResponse>>(owner.Client, "/api/bank-accounts"), a => a.Id == memberAccount.Id);
        Assert.Equal(memberAccount.Id, (await GetAccountAsync(member, memberAccount.Id)).Id);
    }

    [Fact(DisplayName = "Групи: DELETE групи лише власником")]
    public async Task Delete_group()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        var account = await CreateAccountAsync(owner, groupId: group.Id);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(member.Client.DeleteAsync($"/api/groups/{group.Id}")));

        using var response = await owner.Client.DeleteAsync($"/api/groups/{group.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Empty(await GetAsync<List<GroupResponse>>(owner.Client, "/api/groups"));
        Assert.Empty(await GetAsync<List<GroupResponse>>(member.Client, "/api/groups"));
        Assert.Empty(await GetAsync<List<BankAccountResponse>>(member.Client, "/api/bank-accounts"));
        Assert.Equal(account.Id, (await GetAccountAsync(owner, account.Id)).Id);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(owner.Client.DeleteAsync($"/api/groups/{group.Id}")));
    }

    [Fact(DisplayName = "Групи: без токена — 401")]
    public async Task Groups_require_auth()
    {
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/groups")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.PostAsJsonAsync("/api/groups", new CreateGroupRequest("x"))));
    }
}
