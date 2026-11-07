using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class UsersTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Користувачі: адмінські ендпоінти недоступні звичайному користувачу")]
    public async Task Admin_endpoints_forbidden_for_user()
    {
        var user = await NewUserAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(user.Client.GetAsync("/api/users")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(user.Client.GetAsync($"/api/users/{user.Id}")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(user.Client.PostAsJsonAsync(
            "/api/users", new CreateUserRequest("user", "x", "x@test.local", "Passw0rd!", null))));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(user.Client.PatchAsJsonAsync(
            $"/api/users/{user.Id}", new UpdateUserRequest(Role: "admin"))));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(user.Client.DeleteAsync($"/api/users/{Admin.Id}")));

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/users")));
    }

    [Fact(DisplayName = "Користувачі: GET списку і за id")]
    public async Task Get_users()
    {
        var user = await NewUserAsync("Петро");

        var users = await GetAsync<List<UserResponse>>(Admin.Client, "/api/users");
        Assert.Contains(users, u => u.Id == Admin.Id && u.RoleName == "admin");
        Assert.Contains(users, u => u.Id == user.Id && u.Username == user.Username && u.Email == user.Email);

        var single = await GetAsync<UserResponse>(Admin.Client, $"/api/users/{user.Id}");
        Assert.Equal("Петро", single.FullName);
        Assert.True(single.IsActive);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(Admin.Client.GetAsync($"/api/users/{Guid.NewGuid()}")));
    }

    [Fact(DisplayName = "Користувачі: POST створює користувача, який може увійти")]
    public async Task Post_user()
    {
        var username = $"new_{Guid.NewGuid():N}"[..20];
        using var response = await Admin.Client.PostAsJsonAsync(
            "/api/users",
            new CreateUserRequest("admin", username, $"{username}@TEST.local", "Passw0rd!", "Нова Адмінка"));
        var created = await ApiAssert.ReadAsync<UserResponse>(response, HttpStatusCode.Created);

        Assert.Equal("admin", created.RoleName);
        Assert.Equal($"{username}@test.local", created.Email);

        using var anonymous = Factory.CreateAnonymousClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "Passw0rd!"));
        var auth = await ApiAssert.ReadAsync<AuthResponse>(login);
        Assert.Equal(created.Id, auth.UserId);
        Assert.Equal("admin", auth.RoleName);
    }

    [Fact(DisplayName = "Користувачі: POST з невідомою роллю — 400")]
    public async Task Post_user_invalid_role()
    {
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(Admin.Client.PostAsJsonAsync(
            "/api/users", new CreateUserRequest("superuser", "bad_role", "bad_role@test.local", "Passw0rd!", null))));
    }

    [Fact(DisplayName = "Користувачі: PATCH змінює лише передані поля")]
    public async Task Patch_user()
    {
        var user = await NewUserAsync("Старе ім'я");

        using var response = await Admin.Client.PatchAsJsonAsync(
            $"/api/users/{user.Id}", new UpdateUserRequest(FullName: "Нове ім'я", Role: "admin"));
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var updated = await GetAsync<UserResponse>(Admin.Client, $"/api/users/{user.Id}");
        Assert.Equal("Нове ім'я", updated.FullName);
        Assert.Equal("admin", updated.RoleName);
        Assert.Equal(user.Username, updated.Username);
        Assert.Equal(user.Email, updated.Email);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(Admin.Client.PatchAsJsonAsync(
            $"/api/users/{Guid.NewGuid()}", new UpdateUserRequest(FullName: "x"))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(Admin.Client.PatchAsJsonAsync(
            $"/api/users/{user.Id}", new UpdateUserRequest(Role: "root"))));
    }

    [Fact(DisplayName = "Користувачі: PATCH IsActive=false блокує вхід")]
    public async Task Patch_user_deactivate()
    {
        var user = await NewUserAsync();

        await ApiAssert.StatusAsync(
            await Admin.Client.PatchAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(IsActive: false)),
            HttpStatusCode.NoContent);

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Username, user.Password))));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(user.Client.GetAsync("/api/auth/me")));
    }

    [Fact(DisplayName = "Користувачі: PUT вимагає всі поля і замінює пароль")]
    public async Task Put_user()
    {
        var user = await NewUserAsync();
        var newUsername = $"put_{Guid.NewGuid():N}"[..20];

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(Admin.Client.PutAsJsonAsync(
            $"/api/users/{user.Id}", new UpdateUserRequest(Username: newUsername))));

        using var response = await Admin.Client.PutAsJsonAsync(
            $"/api/users/{user.Id}",
            new UpdateUserRequest("user", newUsername, $"{newUsername}@test.local", "N3w-Passw0rd", "Повне Ім'я", true));
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var updated = await GetAsync<UserResponse>(Admin.Client, $"/api/users/{user.Id}");
        Assert.Equal(newUsername, updated.Username);
        Assert.Equal("Повне Ім'я", updated.FullName);

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(newUsername, user.Password))));
        Assert.Equal(HttpStatusCode.OK, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(newUsername, "N3w-Passw0rd"))));

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(Admin.Client.PutAsJsonAsync(
            $"/api/users/{Guid.NewGuid()}",
            new UpdateUserRequest("user", "ghost", "ghost@test.local", "Passw0rd!", null, true))));
    }

    [Fact(DisplayName = "Користувачі: DELETE анонімізує та деактивує користувача")]
    public async Task Delete_user()
    {
        var user = await NewUserAsync("Видалюваний");
        var group = await CreateGroupAsync(Admin);
        await AddMemberAsync(Admin, group.Id, user);

        using var response = await Admin.Client.DeleteAsync($"/api/users/{user.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var deleted = await GetAsync<UserResponse>(Admin.Client, $"/api/users/{user.Id}");
        Assert.False(deleted.IsActive);
        Assert.Equal($"deleted-{user.Id:N}", deleted.Username);
        Assert.Null(deleted.FullName);

        var members = await GetAsync<List<GroupMemberResponse>>(Admin.Client, $"/api/groups/{group.Id}/members");
        Assert.DoesNotContain(members, m => m.UserId == user.Id);

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Username, user.Password))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(Admin.Client.DeleteAsync($"/api/users/{Guid.NewGuid()}")));
    }

    [Fact(DisplayName = "Користувачі: пошук знаходить інших активних користувачів, але не себе")]
    public async Task Search_users()
    {
        var marker = $"Шукач{Guid.NewGuid():N}"[..14];
        var searcher = await NewUserAsync(marker);
        var target = await NewUserAsync($"{marker} ціль");
        var inactive = await NewUserAsync($"{marker} неактивний");
        await ApiAssert.StatusAsync(
            await Admin.Client.PatchAsJsonAsync($"/api/users/{inactive.Id}", new UpdateUserRequest(IsActive: false)),
            HttpStatusCode.NoContent);

        var found = await GetAsync<List<UserResponse>>(searcher.Client, $"/api/users/search?q={Uri.EscapeDataString(marker.ToUpperInvariant())}");

        Assert.Equal([target.Id], found.Select(u => u.Id));

        var byUsername = await GetAsync<List<UserResponse>>(searcher.Client, $"/api/users/search?q={target.Username}");
        Assert.Equal([target.Id], byUsername.Select(u => u.Id));

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/users/search?q=a")));
    }
}
