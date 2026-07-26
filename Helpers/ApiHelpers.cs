using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;

internal static class ApiHelpers
{
    internal const string AppSettingsFileName = "app-settings.json";
internal static Guid? GetUserIdFromPrincipal(ClaimsPrincipal principal)
{
    var userIdRaw =
        principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
        principal.FindFirstValue("sub");

    return Guid.TryParse(userIdRaw, out var userId) ? userId : null;
}

internal static UserRole? NormalizeRole(string? role)
{
    var normalized = role?.Trim().ToLowerInvariant();
    return normalized switch
    {
        "admin" => UserRole.Admin,
        "user" => UserRole.User,
        _ => null
    };
}

internal static CategoryType? NormalizeCategoryType(string? type)
{
    var normalized = type?.Trim().ToLowerInvariant();
    return normalized switch
    {
        "income" => CategoryType.Income,
        "expense" => CategoryType.Expense,
        _ => null
    };
}

internal static string NormalizeIconKey(string? iconKey, CategoryType categoryType)
{
    var normalized = (iconKey ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return categoryType == CategoryType.Income ? "income" : "other";
    }

    return normalized.Length > 50 ? normalized[..50] : normalized;
}

internal static string NormalizeGroupIconKey(string? iconKey)
{
    var normalized = (iconKey ?? string.Empty).Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "other";
    }

    return normalized.Length > 50 ? normalized[..50] : normalized;
}

internal static string NormalizeSavingIconKey(string? iconKey)
{
    var normalized = (iconKey ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "other";
    }

    return normalized.Length > 50 ? normalized[..50] : normalized;
}

internal static string NormalizeCurrency(string? currency)
{
    var normalized = (currency ?? "UAH").Trim().ToUpperInvariant();
    return normalized.Length == 3 ? normalized : "UAH";
}

internal static string? NormalizeColor(string? color)
{
    var normalized = (color ?? string.Empty).Trim();
    return string.IsNullOrWhiteSpace(normalized)
        ? null
        : normalized.Length > 10 ? normalized[..10] : normalized;
}

internal static string ToRoleName(UserRole role) =>
    role == UserRole.Admin ? "admin" : "user";

internal static async Task<BankAccountResponse> ToBankAccountResponseAsync(BankAccount account, AppDbContext db, CancellationToken ct)
{
    var sharedBy = await db.Users
        .AsNoTracking()
        .Where(u => u.Id == account.UserId)
                .Select(u => u.IsActive ? u.Username : null)
        .FirstOrDefaultAsync(ct);

    return new(
        account.Id,
        account.UserId,
        null,
        account.Name,
        account.Currency,
        account.Balance,
        account.IsDefault,
        null,
        null,
        sharedBy);
}

internal static SavingResponse ToSavingResponse(Saving saving) =>
    new(saving.Id, saving.UserId, null, saving.Name, saving.TargetAmount, saving.CurrentAmount, saving.Deadline, saving.Currency, saving.IconKey, saving.Color, saving.IsCompleted);

internal static SavingItemResponse ToSavingItemResponse(SavingItem item) =>
    new(item.Id, item.SavingId, item.Name, item.Price, item.Priority, item.IsPurchased);

internal static async Task<int> InsertScheduledPaymentAsync(
    AppDbContext db,
    string name,
    TimeSpan repeatInterval,
    DateTimeOffset nextDueDate,
    CancellationToken ct)
{
    var ids = await db.Database.SqlQueryRaw<int>(
            """
            INSERT INTO recurring_payments (name, repeat_interval, next_due_date, is_active)
            VALUES (@name, CAST(@repeat_interval AS interval), @next_due_date, TRUE)
            RETURNING id AS "Value"
            """,
            new NpgsqlParameter("name", name),
            new NpgsqlParameter("repeat_interval", FormatRepeatIntervalForPostgres(repeatInterval)),
            new NpgsqlParameter("next_due_date", nextDueDate))
        .ToListAsync(ct);

    return ids.Single();
}

internal static async Task UpdateScheduledPaymentIntervalAsync(
    AppDbContext db,
    int scheduledPaymentId,
    TimeSpan repeatInterval,
    CancellationToken ct)
{
    await db.Database.ExecuteSqlRawAsync(
        """
        UPDATE recurring_payments
        SET repeat_interval = CAST(@repeat_interval AS interval)
        WHERE id = @id
        """,
        [
            new NpgsqlParameter("repeat_interval", FormatRepeatIntervalForPostgres(repeatInterval)),
            new NpgsqlParameter("id", scheduledPaymentId)
        ],
        ct);
}

internal static async Task<TimeSpan> GetRepeatIntervalAsync(AppDbContext db, int scheduledPaymentId, CancellationToken ct)
{
    var seconds = await db.Database.SqlQueryRaw<double>(
            """
            SELECT (
                EXTRACT(YEAR FROM repeat_interval) * 365 * 86400
                + EXTRACT(MONTH FROM repeat_interval) * 30 * 86400
                + EXTRACT(DAY FROM repeat_interval) * 86400
                + EXTRACT(HOUR FROM repeat_interval) * 3600
                + EXTRACT(MINUTE FROM repeat_interval) * 60
                + EXTRACT(SECOND FROM repeat_interval)
            )::double precision AS "Value"
            FROM recurring_payments
            WHERE id = @id
            """,
            new NpgsqlParameter("id", scheduledPaymentId))
        .FirstOrDefaultAsync(ct);

    return TimeSpan.FromSeconds(seconds);
}

internal static string FormatRepeatIntervalForPostgres(TimeSpan repeatInterval)
{
    if (repeatInterval <= TimeSpan.Zero)
    {
        return "0 seconds";
    }

    if (repeatInterval == TimeSpan.FromDays(30))
    {
        return "1 month";
    }

    if (repeatInterval == TimeSpan.FromDays(365))
    {
        return "1 year";
    }

    if (repeatInterval == TimeSpan.FromDays(7))
    {
        return "7 days";
    }

    if (repeatInterval == TimeSpan.FromDays(1))
    {
        return "1 day";
    }

    return FormattableString.Invariant($"{repeatInterval.TotalSeconds:0.######} seconds");
}

internal static DateOnly AddRepeatInterval(DateOnly dueDate, TimeSpan repeatInterval)
{
    var date = dueDate.ToDateTime(TimeOnly.MinValue);
    return repeatInterval.TotalDays switch
    {
        >= 28 and <= 31 => DateOnly.FromDateTime(date.AddMonths(1)),
        >= 365 and <= 366 => DateOnly.FromDateTime(date.AddYears(1)),
        _ => DateOnly.FromDateTime(date.Add(repeatInterval))
    };
}

internal static DateOnly ToDateOnly(DateTimeOffset value) =>
    DateOnly.FromDateTime(value.UtcDateTime);

internal static DateTimeOffset ToUtcDateTimeOffset(DateOnly value) =>
    new(value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

internal static TransactionResponse ToTransactionResponse(Transaction transaction) =>
    new(transaction.Id, transaction.AccountId, null, null, transaction.Account?.User is null || !transaction.Account.User.IsActive
            ? null
            : transaction.Account.User.Username, transaction.CategoryId, transaction.SavingId, transaction.RecurringPaymentId, transaction.Amount,
        transaction.Description, transaction.TransactionDate);

internal static async Task SetAccountGroupAccessAsync(AppDbContext db, Guid accountId, Guid groupId, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO group_resource_access (group_id, account_id, shared_by)
        VALUES ({groupId}, {accountId}, {sharedBy})
        ON CONFLICT (group_id, account_id) DO UPDATE SET shared_by = EXCLUDED.shared_by;
        """, ct);
}

internal static async Task ReplaceAccountGroupAccessAsync(AppDbContext db, Guid accountId, Guid? groupId, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_resource_access WHERE account_id = {accountId};", ct);
    if (groupId.HasValue)
    {
        await SetAccountGroupAccessAsync(db, accountId, groupId.Value, sharedBy, ct);
    }
}

internal static async Task SetSavingGroupAccessAsync(AppDbContext db, int savingId, Guid groupId, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO group_resource_access (group_id, saving_id, shared_by)
        VALUES ({groupId}, {savingId}, {sharedBy})
        ON CONFLICT (group_id, saving_id) DO UPDATE SET shared_by = EXCLUDED.shared_by;
        """, ct);
}

internal static async Task ReplaceSavingGroupAccessAsync(AppDbContext db, int savingId, Guid? groupId, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_resource_access WHERE saving_id = {savingId};", ct);
    if (groupId.HasValue)
    {
        await SetSavingGroupAccessAsync(db, savingId, groupId.Value, sharedBy, ct);
    }
}

internal static async Task ReplaceSavingGroupAccessesAsync(AppDbContext db, int savingId, IReadOnlyCollection<Guid> groupIds, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_resource_access WHERE saving_id = {savingId};", ct);
    foreach (var groupId in groupIds)
    {
        await SetSavingGroupAccessAsync(db, savingId, groupId, sharedBy, ct);
    }
}

internal static async Task ReplaceAccountGroupAccessesAsync(AppDbContext db, Guid accountId, IReadOnlyCollection<Guid> groupIds, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_resource_access WHERE account_id = {accountId};", ct);
    foreach (var groupId in groupIds)
    {
        await SetAccountGroupAccessAsync(db, accountId, groupId, sharedBy, ct);
    }
}

internal static async Task SetTransactionGroupAccessAsync(AppDbContext db, int transactionId, Guid groupId, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO group_resource_access (group_id, transaction_id, shared_by)
        VALUES ({groupId}, {transactionId}, {sharedBy})
        ON CONFLICT (group_id, transaction_id) DO UPDATE SET shared_by = EXCLUDED.shared_by;
        """, ct);
}

internal static async Task ReplaceTransactionGroupAccessAsync(AppDbContext db, int transactionId, Guid? groupId, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_resource_access WHERE transaction_id = {transactionId};", ct);
    if (groupId.HasValue)
    {
        await SetTransactionGroupAccessAsync(db, transactionId, groupId.Value, sharedBy, ct);
    }
}

internal static async Task ReplaceTransactionGroupAccessesAsync(AppDbContext db, int transactionId, IReadOnlyCollection<Guid> groupIds, Guid sharedBy, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_resource_access WHERE transaction_id = {transactionId};", ct);
    foreach (var groupId in groupIds)
    {
        await SetTransactionGroupAccessAsync(db, transactionId, groupId, sharedBy, ct);
    }
}

internal static async Task<bool> CanUseSavingAsync(AppDbContext db, int savingId, Guid userId, bool requireManage, CancellationToken ct) =>
    await db.Savings.AnyAsync(s => s.Id == savingId && (s.UserId == userId ||
        db.GroupResourceAccess.Any(access => access.SavingId == s.Id &&
            db.GroupMembers.Any(m => m.GroupId == access.GroupId &&
                m.UserId == userId &&
                (!requireManage || m.Role != UserGroupRole.Viewer)))), ct);

internal static async Task DeleteOwnedGroupAccessAsync(AppDbContext db, Guid groupId, Guid ownerUserId, CancellationToken ct)
{
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        DELETE FROM group_resource_access access
        USING accounts account
        WHERE access.group_id = {groupId}
          AND access.account_id = account.id
          AND account.user_id = {ownerUserId};

        DELETE FROM group_resource_access access
        USING savings saving
        WHERE access.group_id = {groupId}
          AND access.saving_id = saving.id
          AND saving.user_id = {ownerUserId};

        DELETE FROM group_resource_access access
        USING transactions tx
        JOIN accounts account ON account.id = tx.account_id
        WHERE access.group_id = {groupId}
          AND access.transaction_id = tx.id
          AND account.user_id = {ownerUserId}
          AND tx.recurring_payments_id IS NOT NULL;
        """, ct);
}

internal static BudgetResponse ToBudgetResponse(Budget budget) =>
    new(
        budget.Id,
        budget.Account?.UserId ?? Guid.Empty,
        budget.GroupId,
        budget.CategoryId,
        budget.Amount,
        null,
        DateOnly.FromDateTime(DateTime.UtcNow),
        true);

internal static async Task ClearDefaultBankAccountsAsync(AppDbContext db, Guid userId, CancellationToken ct)
{
    var defaultAccounts = await db.BankAccounts
        .Where(a => a.UserId == userId && a.IsDefault)
        .ToListAsync(ct);

    foreach (var defaultAccount in defaultAccounts)
    {
        defaultAccount.IsDefault = false;
    }
}

internal static UserGroupRole ParseGroupRole(string roleName) =>
    roleName.Trim().ToLowerInvariant() switch
    {
        "owner" => UserGroupRole.Owner,
        "viewer" => UserGroupRole.Viewer,
        _ => UserGroupRole.Member
    };

internal static SameSiteMode ParseSameSiteMode(string? configuredValue) =>
    configuredValue?.Trim().ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax
    };

internal static string? GetRequesterIp(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString();

internal static async Task SetAuditContextAsync(AppDbContext db, Guid userId, string? device, CancellationToken ct)
{
    if (!db.Database.IsRelational())
    {
        return;
    }

    await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT set_config('app.current_user_id', {userId.ToString()}, false), set_config('app.device', {device ?? string.Empty}, false);",
        ct);
}

internal static async Task EnsureDatabaseAndSchemaAsync(IConfiguration configuration, IHostEnvironment environment, CancellationToken ct)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("DefaultConnection is missing.");
    }

    var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString);
    var databaseName = connectionStringBuilder.Database;
    if (string.IsNullOrWhiteSpace(databaseName))
    {
        throw new InvalidOperationException("DefaultConnection must include a Database name.");
    }

    var masterBuilder = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Database = "postgres"
    };

    await using (var masterConnection = new NpgsqlConnection(masterBuilder.ConnectionString))
    {
        await masterConnection.OpenAsync(ct);

        await using (var existsCommand = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", masterConnection))
        {
            existsCommand.Parameters.AddWithValue("name", databaseName);
            var exists = await existsCommand.ExecuteScalarAsync(ct) is not null;
            if (!exists)
            {
                var quotedName = QuoteIdentifier(databaseName);
                await using var createCommand = new NpgsqlCommand($"CREATE DATABASE {quotedName}", masterConnection);
                await createCommand.ExecuteNonQueryAsync(ct);
            }
        }
    }

    await using var dbConnection = new NpgsqlConnection(connectionString);
    await dbConnection.OpenAsync(ct);

    // We consider the schema "initialized" only when several core tables exist.
    // If users exists but other tables are missing, we fail fast with an actionable message
    // rather than trying to re-run a non-idempotent bootstrap script.
    var schemaIsInitialized = false;

    await using (var schemaCheck = new NpgsqlCommand("""
        SELECT
            to_regclass('public.users')::text        AS users_table,
            to_regclass('public.accounts')::text     AS accounts_table,
            to_regclass('public.categories')::text   AS categories_table,
            to_regclass('public.transactions')::text AS transactions_table,
            to_regclass('public.refresh_tokens')::text AS refresh_tokens_table
    """, dbConnection))
    {
        await using var reader = await schemaCheck.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var usersTable = reader.IsDBNull(0) ? null : reader.GetString(0);
        var accountsTable = reader.IsDBNull(1) ? null : reader.GetString(1);
        var categoriesTable = reader.IsDBNull(2) ? null : reader.GetString(2);
        var transactionsTable = reader.IsDBNull(3) ? null : reader.GetString(3);
        var refreshTokensTable = reader.IsDBNull(4) ? null : reader.GetString(4);

        var hasAnyCoreTable =
            !string.IsNullOrWhiteSpace(usersTable) ||
            !string.IsNullOrWhiteSpace(accountsTable) ||
            !string.IsNullOrWhiteSpace(categoriesTable) ||
            !string.IsNullOrWhiteSpace(transactionsTable) ||
            !string.IsNullOrWhiteSpace(refreshTokensTable);

        var hasAllCoreTables =
            !string.IsNullOrWhiteSpace(usersTable) &&
            !string.IsNullOrWhiteSpace(accountsTable) &&
            !string.IsNullOrWhiteSpace(categoriesTable) &&
            !string.IsNullOrWhiteSpace(transactionsTable) &&
            !string.IsNullOrWhiteSpace(refreshTokensTable);

        if (hasAllCoreTables)
        {
            schemaIsInitialized = true;
        }
        else if (hasAnyCoreTable)
        {
            throw new InvalidOperationException(
                $"Database '{databaseName}' exists but is only partially initialized. " +
                "Drop the database and re-run the API to bootstrap it from Postgre.sql.");
        }
    }

    if (schemaIsInitialized)
    {
        await EnsureSchemaCompatibilityAsync(dbConnection, ct);
        return;
    }

    var candidatePaths = new[]
    {
        Path.Combine(environment.ContentRootPath, "DataBase", "Postgre.sql"),
        Path.Combine(environment.ContentRootPath, "Postgre.sql"),
        Path.Combine(AppContext.BaseDirectory, "DataBase", "Postgre.sql"),
        Path.Combine(AppContext.BaseDirectory, "Postgre.sql")
    };

    var scriptPath = candidatePaths.FirstOrDefault(File.Exists);
    if (scriptPath is null)
    {
        throw new FileNotFoundException("Postgre.sql was not found. Ensure it exists at KeepWalletAPI/DataBase/Postgre.sql and is copied to the output directory.");
    }

    var script = await File.ReadAllTextAsync(scriptPath, ct);
    await using var scriptCommand = new NpgsqlCommand(script, dbConnection)
    {
        CommandTimeout = 0
    };
    await scriptCommand.ExecuteNonQueryAsync(ct);
    await EnsureSchemaCompatibilityAsync(dbConnection, ct);
}

internal static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

internal static async Task EnsureSchemaCompatibilityAsync(NpgsqlConnection dbConnection, CancellationToken ct)
{
    await using var command = new NpgsqlCommand("""
        ALTER TABLE IF EXISTS groups
        ADD COLUMN IF NOT EXISTS color VARCHAR(10);

        CREATE TABLE IF NOT EXISTS group_resource_access (
            group_id       UUID NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
            account_id     UUID REFERENCES accounts(id) ON DELETE CASCADE,
            saving_id      INT REFERENCES savings(id) ON DELETE CASCADE,
            transaction_id INT REFERENCES transactions(id) ON DELETE CASCADE,
            shared_by      UUID REFERENCES users(id) ON DELETE SET NULL,
            CHECK (
                (account_id IS NOT NULL)::int +
                (saving_id IS NOT NULL)::int +
                (transaction_id IS NOT NULL)::int = 1
            ),
            UNIQUE (group_id, account_id),
            UNIQUE (group_id, saving_id),
            UNIQUE (group_id, transaction_id)
        );

        CREATE INDEX IF NOT EXISTS idx_group_resource_access_group_id
            ON group_resource_access(group_id);
        CREATE INDEX IF NOT EXISTS idx_group_resource_access_account_id
            ON group_resource_access(account_id);
        CREATE INDEX IF NOT EXISTS idx_group_resource_access_saving_id
            ON group_resource_access(saving_id);
        CREATE INDEX IF NOT EXISTS idx_group_resource_access_transaction_id
            ON group_resource_access(transaction_id);

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'accounts' AND column_name = 'group_id'
            ) THEN
                INSERT INTO group_resource_access (group_id, account_id, shared_by)
                SELECT group_id, id, user_id
                FROM accounts
                WHERE group_id IS NOT NULL
                ON CONFLICT (group_id, account_id) DO NOTHING;

                ALTER TABLE accounts DROP COLUMN group_id;
            END IF;

            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'savings' AND column_name = 'group_id'
            ) THEN
                INSERT INTO group_resource_access (group_id, saving_id, shared_by)
                SELECT group_id, id, user_id
                FROM savings
                WHERE group_id IS NOT NULL
                ON CONFLICT (group_id, saving_id) DO NOTHING;

                ALTER TABLE savings DROP COLUMN group_id;
            END IF;

            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'transactions' AND column_name = 'group_id'
            ) THEN
                INSERT INTO group_resource_access (group_id, transaction_id, shared_by)
                SELECT t.group_id, t.id, a.user_id
                FROM transactions t
                JOIN accounts a ON a.id = t.account_id
                WHERE t.group_id IS NOT NULL
                ON CONFLICT (group_id, transaction_id) DO NOTHING;

                ALTER TABLE transactions DROP COLUMN group_id;
            END IF;
        END $$;
    """, dbConnection)
    {
        CommandTimeout = 0
    };
    await command.ExecuteNonQueryAsync(ct);
}

internal static async Task<bool> IsRegistrationEnabledAsync(string contentRootPath, CancellationToken ct)
{
    var settings = await ReadFileAppSettingsAsync(contentRootPath, ct);
    return settings.RegistrationEnabled;
}

internal static async Task SetRegistrationEnabledAsync(string contentRootPath, bool enabled, CancellationToken ct)
{
    var settings = await ReadFileAppSettingsAsync(contentRootPath, ct);
    settings = settings with { RegistrationEnabled = enabled };
    await WriteFileAppSettingsAsync(contentRootPath, settings, ct);
}

internal static async Task<FileAppSettings> ReadFileAppSettingsAsync(string contentRootPath, CancellationToken ct)
{
    var path = GetAppSettingsFilePath(contentRootPath);
    if (!File.Exists(path))
    {
        var defaults = new FileAppSettings(true);
        await WriteFileAppSettingsAsync(contentRootPath, defaults, ct);
        return defaults;
    }

    await using var stream = File.OpenRead(path);
    var settings = await JsonSerializer.DeserializeAsync<FileAppSettings>(stream, CreateAppSettingsJsonOptions(), ct);
    return settings ?? new FileAppSettings(true);
}

internal static async Task WriteFileAppSettingsAsync(string contentRootPath, FileAppSettings settings, CancellationToken ct)
{
    var path = GetAppSettingsFilePath(contentRootPath);
    var tempPath = $"{path}.tmp";
    await using (var stream = File.Create(tempPath))
    {
        await JsonSerializer.SerializeAsync(stream, settings, CreateAppSettingsJsonOptions(), ct);
    }

    File.Move(tempPath, path, true);
}

internal static string GetAppSettingsFilePath(string contentRootPath) => Path.Combine(contentRootPath, AppSettingsFileName);

internal static JsonSerializerOptions CreateAppSettingsJsonOptions() => new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
};

internal static void AppendAuthCookies(
    HttpContext context,
    string accessCookieName,
    string refreshCookieName,
    string accessToken,
    DateTimeOffset accessTokenExpiresAt,
    string refreshToken,
    DateTimeOffset refreshTokenExpiresAt,
    bool secureCookies,
    SameSiteMode sameSiteMode,
    string refreshCookiePath)
{
    context.Response.Cookies.Append(accessCookieName, accessToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secureCookies,
        SameSite = sameSiteMode,
        Expires = accessTokenExpiresAt
    });

    context.Response.Cookies.Append(refreshCookieName, refreshToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = secureCookies,
        SameSite = sameSiteMode,
        Expires = refreshTokenExpiresAt,
        Path = refreshCookiePath
    });
}

internal static void ClearAuthCookies(
    HttpContext context,
    string accessCookieName,
    string refreshCookieName,
    bool secureCookies,
    SameSiteMode sameSiteMode,
    string refreshCookiePath)
{
    context.Response.Cookies.Delete(accessCookieName, new CookieOptions
    {
        Secure = secureCookies,
        SameSite = sameSiteMode
    });

    context.Response.Cookies.Delete(refreshCookieName, new CookieOptions
    {
        Secure = secureCookies,
        SameSite = sameSiteMode,
        Path = refreshCookiePath
    });
}

public sealed record FileAppSettings(bool RegistrationEnabled);
}
