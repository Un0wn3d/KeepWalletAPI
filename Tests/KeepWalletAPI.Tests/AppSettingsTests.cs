using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class AppSettingsTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Налаштування: GET ролей доступний без авторизації")]
    public async Task Get_roles()
    {
        using var anonymous = Factory.CreateAnonymousClient();
        var roles = await GetAsync<List<JsonElement>>(anonymous, "/api/roles");

        Assert.Equal(["admin", "user"], roles.Select(r => r.GetProperty("name").GetString()));
    }

    [Fact(DisplayName = "Налаштування: GET налаштувань застосунку")]
    public async Task Get_app_settings()
    {
        using var anonymous = Factory.CreateAnonymousClient();
        var settings = await GetAsync<AppSettingsResponse>(anonymous, "/api/app-settings");

        Assert.True(settings.RegistrationEnabled);
    }

    [Fact(DisplayName = "Налаштування: PATCH дозволений лише адміністратору")]
    public async Task Patch_app_settings_requires_admin()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();
        var request = new UpdateAppSettingsRequest(false);

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.PatchAsJsonAsync("/api/app-settings", request)));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(user.Client.PatchAsJsonAsync("/api/app-settings", request)));

        var settings = await GetAsync<AppSettingsResponse>(anonymous, "/api/app-settings");
        Assert.True(settings.RegistrationEnabled);
    }

    [Fact(DisplayName = "Налаштування: вимкнення реєстрації блокує нових користувачів")]
    public async Task Patch_app_settings_disables_registration()
    {
        using var anonymous = Factory.CreateAnonymousClient();
        try
        {
            using var disable = await Admin.Client.PatchAsJsonAsync("/api/app-settings", new UpdateAppSettingsRequest(false));
            var disabled = await ApiAssert.ReadAsync<AppSettingsResponse>(disable);
            Assert.False(disabled.RegistrationEnabled);
            Assert.False((await GetAsync<AppSettingsResponse>(anonymous, "/api/app-settings")).RegistrationEnabled);
            Assert.Contains("false", await File.ReadAllTextAsync(Factory.AppSettingsPath));

            var username = $"blocked_{Guid.NewGuid():N}"[..20];
            Assert.Equal(HttpStatusCode.Forbidden, await StatusOfAsync(anonymous.PostAsJsonAsync(
                "/api/auth/register",
                new RegisterRequest(username, $"{username}@test.local", "Passw0rd!", null))));
        }
        finally
        {
            await ApiAssert.StatusAsync(
                await Admin.Client.PatchAsJsonAsync("/api/app-settings", new UpdateAppSettingsRequest(true)),
                HttpStatusCode.OK);
        }

        await NewUserAsync();
    }
}
