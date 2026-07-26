using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class AppSettingsEndpoints
{
    internal static void MapAppSettingsEndpoints(this WebApplication app)
    {
app.MapGet("/api/roles", () => Results.Ok(new[]
{
    new { Name = "admin", Description = "Administrator" },
    new { Name = "user", Description = "Regular user" }
}));

app.MapGet("/api/app-settings", async (IWebHostEnvironment env, CancellationToken ct) =>
{
    return Results.Ok(new AppSettingsResponse(await IsRegistrationEnabledAsync(env.ContentRootPath, ct)));
});

app.MapPatch("/api/app-settings", async (
    UpdateAppSettingsRequest request,
    IWebHostEnvironment env,
    CancellationToken ct) =>
{
    await SetRegistrationEnabledAsync(env.ContentRootPath, request.RegistrationEnabled, ct);
    return Results.Ok(new AppSettingsResponse(request.RegistrationEnabled));
}).RequireAuthorization("AdminOnly");

    }
}
