using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace KeepWalletAPI.Tests.Infrastructure;

/// <summary>
/// Піднімає справжній KeepWalletAPI у пам'яті поверх окремої тимчасової бази PostgreSQL.
/// База створюється з DataBase/Postgre.sql при старті і видаляється після прогону.
/// Робоча база KeepWallet і файл API/app-settings.json не змінюються.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Можна перевизначити змінною середовища, якщо Postgres слухає інший хост/порт або має інший пароль.
    private const string ServerConnectionVariable = "KEEPWALLET_TEST_POSTGRES";
    private const string DefaultServerConnection = "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private readonly string databaseName = $"keepwallet_tests_{Guid.NewGuid():N}";
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), $"keepwallet-api-tests-{Guid.NewGuid():N}");
    private readonly string serverConnectionString;

    public ApiFactory()
    {
        serverConnectionString = Environment.GetEnvironmentVariable(ServerConnectionVariable) ?? DefaultServerConnection;
        ConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = databaseName }.ConnectionString;

        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(AppSettingsPath, """{ "registrationEnabled": true }""");

        // Program.cs читає ці значення ще до builder.Build(), тому вони передаються через змінні середовища.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Key", "keepwallet-integration-tests-signing-key-0123456789");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "KeepWalletAPI");
        Environment.SetEnvironmentVariable("Jwt__Audience", "KeepWalletClient");
        Environment.SetEnvironmentVariable("Auth__UseSecureCookies", "false");
    }

    public string ConnectionString { get; }

    public string AppSettingsPath => Path.Combine(contentRoot, "app-settings.json");

    /// <summary>Перший зареєстрований користувач, тому він має роль admin.</summary>
    public ApiUser Admin { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseContentRoot(contentRoot);
    }

    public HttpClient CreateAnonymousClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public async Task<ApiUser> RegisterUserAsync(string? fullName = null)
    {
        // Стовпці username/email у БД — VARCHAR(45), тому тримаємо значення короткими.
        var username = $"u_{Guid.NewGuid():N}"[..20];
        var password = "Str0ng-Passw0rd!";
        var email = $"{username}@Test.Local";

        using var anonymous = CreateAnonymousClient();
        using var response = await anonymous.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(username, email, password, fullName));
        var auth = await ApiAssert.ReadAsync<AuthResponse>(response, HttpStatusCode.Created);

        return new ApiUser(auth.UserId, username, email.ToLowerInvariant(), password, auth.RoleName, CreateAuthorizedClient(auth.AccessToken));
    }

    public HttpClient CreateAuthorizedClient(string accessToken)
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async Task InitializeAsync()
    {
        await DropDatabaseAsync();
        Admin = await RegisterUserAsync("Test Admin");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await DropDatabaseAsync();

        try
        {
            Directory.Delete(contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // Тимчасова папка не критична, Windows може ще тримати файл.
        }
    }

    private async Task DropDatabaseAsync()
    {
        var masterConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record ApiUser(Guid Id, string Username, string Email, string Password, string? RoleName, HttpClient Client);

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "KeepWallet API";
}
