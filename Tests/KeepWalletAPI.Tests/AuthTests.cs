using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class AuthTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Auth: перший користувач стає адміністратором")]
    public void First_user_is_admin()
    {
        Assert.Equal("admin", Admin.RoleName);
    }

    [Fact(DisplayName = "Auth: реєстрація повертає 201, токен і нормалізований email")]
    public async Task Register_creates_user()
    {
        var username = $"Reg_{Guid.NewGuid():N}"[..16];
        using var anonymous = Factory.CreateAnonymousClient();

        using var response = await anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"  {username}  ", $"  {username}@Example.COM ", "Passw0rd!", "  Олена Тест  "));
        var auth = await ApiAssert.ReadAsync<AuthResponse>(response, HttpStatusCode.Created);

        Assert.Equal($"/api/users/{auth.UserId}", response.Headers.Location?.OriginalString);
        Assert.Equal(username, auth.Username);
        Assert.Equal($"{username}@example.com".ToLowerInvariant(), auth.Email);
        Assert.Equal("user", auth.RoleName);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.True(auth.AccessTokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(auth.RefreshTokenExpiresAt > auth.AccessTokenExpiresAt);

        using var client = Factory.CreateAuthorizedClient(auth.AccessToken);
        var me = await GetAsync<JsonElement>(client, "/api/auth/me");
        Assert.Equal(auth.UserId, me.GetProperty("id").GetGuid());
        Assert.Equal("Олена Тест", me.GetProperty("fullName").GetString());
        Assert.Equal("user", me.GetProperty("roleName").GetString());
    }

    [Fact(DisplayName = "Auth: занадто довгі логін або email — 400, а не 500")]
    public async Task Register_too_long_fields_bad_request()
    {
        // Контракт дозволяє 100/255 символів, але в БД стовпці VARCHAR(45).
        using var anonymous = Factory.CreateAnonymousClient();
        var longUsername = $"long_{Guid.NewGuid():N}{Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(longUsername, $"{Guid.NewGuid():N}"[..10] + "@test.local", "Passw0rd!", null))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"{Guid.NewGuid():N}"[..10], $"{longUsername}@test.local", "Passw0rd!", null))));
    }

    [Fact(DisplayName = "Auth: повторна реєстрація з тим самим логіном або email — 409")]
    public async Task Register_duplicate_conflict()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Conflict, await StatusOfAsync(anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(user.Username.ToUpperInvariant(), $"other_{Guid.NewGuid():N}@test.local", "Passw0rd!", null))));
        Assert.Equal(HttpStatusCode.Conflict, await StatusOfAsync(anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"other_{Guid.NewGuid():N}", user.Email.ToUpperInvariant(), "Passw0rd!", null))));
    }

    [Fact(DisplayName = "Auth: вхід за логіном і за email")]
    public async Task Login_by_username_and_email()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();

        foreach (var login in new[] { user.Username, user.Email.ToUpperInvariant() })
        {
            using var response = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, user.Password));
            var auth = await ApiAssert.ReadAsync<AuthResponse>(response);
            Assert.Equal(user.Id, auth.UserId);

            using var client = Factory.CreateAuthorizedClient(auth.AccessToken);
            await ApiAssert.StatusAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.OK);
        }
    }

    [Fact(DisplayName = "Auth: невірний пароль або невідомий користувач — 401")]
    public async Task Login_invalid_credentials()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Username, "wrong-password"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest($"nobody_{Guid.NewGuid():N}", "whatever"))));
    }

    [Fact(DisplayName = "Auth: /me без токена або з підробленим токеном — 401")]
    public async Task Me_requires_valid_token()
    {
        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/auth/me")));

        using var forged = Factory.CreateAuthorizedClient("not.a.jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(forged.GetAsync("/api/auth/me")));
    }

    [Fact(DisplayName = "Auth: refresh видає новий токен і робить старий refresh-токен недійсним")]
    public async Task Refresh_rotates_tokens()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Username, user.Password));
        await ApiAssert.StatusAsync(login, HttpStatusCode.OK);
        var oldRefreshCookie = GetCookie(login, "refresh_token");
        Assert.NotNull(GetCookie(login, "access_token"));

        using var refresh = await SendWithCookieAsync(anonymous, "/api/auth/refresh", oldRefreshCookie);
        var refreshed = await ApiAssert.ReadAsync<RefreshResponse>(refresh);
        var newRefreshCookie = GetCookie(refresh, "refresh_token");
        Assert.NotEqual(oldRefreshCookie, newRefreshCookie);

        using var client = Factory.CreateAuthorizedClient(refreshed.AccessToken);
        var me = await GetAsync<JsonElement>(client, "/api/auth/me");
        Assert.Equal(user.Id, me.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(SendWithCookieAsync(anonymous, "/api/auth/refresh", oldRefreshCookie)));
        Assert.Equal(HttpStatusCode.OK, await StatusOfAsync(SendWithCookieAsync(anonymous, "/api/auth/refresh", newRefreshCookie)));
    }

    [Fact(DisplayName = "Auth: refresh без cookie — 401")]
    public async Task Refresh_without_cookie()
    {
        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.PostAsync("/api/auth/refresh", null)));
    }

    [Fact(DisplayName = "Auth: logout видаляє refresh-токен і очищає cookie")]
    public async Task Logout_revokes_refresh_token()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Username, user.Password));
        var refreshCookie = GetCookie(login, "refresh_token");

        using var logout = await SendWithCookieAsync(anonymous, "/api/auth/logout", refreshCookie);
        await ApiAssert.StatusAsync(logout, HttpStatusCode.OK);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), header => header.StartsWith("refresh_token=;", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(SendWithCookieAsync(anonymous, "/api/auth/refresh", refreshCookie)));
    }

    [Fact(DisplayName = "Auth: access-токен працює також з cookie")]
    public async Task Access_cookie_authenticates()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();
        using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Username, user.Password));
        var accessCookie = GetCookie(login, "access_token");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", $"access_token={accessCookie}");
        using var response = await anonymous.SendAsync(request);
        var me = await ApiAssert.ReadAsync<JsonElement>(response);
        Assert.Equal(user.Id, me.GetProperty("id").GetGuid());
    }

    private static string GetCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith($"{name}=", StringComparison.Ordinal));
        var value = header[(name.Length + 1)..].Split(';')[0];
        Assert.False(string.IsNullOrWhiteSpace(value), $"Cookie {name} порожня.");
        return value;
    }

    private static Task<HttpResponseMessage> SendWithCookieAsync(HttpClient client, string url, string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        return client.SendAsync(request);
    }
}
