using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class AuthEndpoints
{
    internal static void MapAuthEndpoints(this WebApplication app, string accessCookieName, string refreshCookieName, string refreshCookiePath, SameSiteMode cookieSameSiteMode, bool useSecureCookies)
    {
app.MapPost("/api/auth/register", async (
    HttpContext context,
    RegisterRequest request,
    AppDbContext db,
    PasswordHasher hasher,
    JwtTokenService jwtTokenService,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    var username = request.Username.Trim();
    var usernameLower = username.ToLowerInvariant();
    var email = request.Email.Trim().ToLowerInvariant();

    await using var registrationTx = await db.Database.BeginTransactionAsync(ct);
    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7301985);", ct);
    var isFirstUser = !await db.Users.AsNoTracking().AnyAsync(ct);
    if (!isFirstUser && !await IsRegistrationEnabledAsync(app.Environment.ContentRootPath, ct))
    {
        return Results.Json(new { message = "Registration is disabled." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var exists = await db.Users.AnyAsync(
        u => u.Email == email || u.Username.ToLower() == usernameLower,
        ct);

    if (exists)
    {
        return Results.Conflict(new { message = "User with this username or email already exists." });
    }

    var user = new User
    {
        Id = Guid.NewGuid(),
        Role = isFirstUser ? UserRole.Admin : UserRole.User,
        Username = username,
        Email = email,
        PasswordHash = hasher.Hash(request.Password),
        FullName = request.FullName?.Trim(),
        IsActive = true
    };

    await SetAuditContextAsync(db, user.Id, GetRequesterIp(context), ct);
    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    await registrationTx.CommitAsync(ct);

    var accessTokenResult = jwtTokenService.CreateToken(user, ToRoleName(user.Role));
    var refreshTokenResult = refreshTokenService.CreateToken(user.Id, GetRequesterIp(context));

    await SetAuditContextAsync(db, user.Id, GetRequesterIp(context), ct);
    db.RefreshTokens.Add(refreshTokenResult.StoredToken);
    await db.SaveChangesAsync(ct);

    AppendAuthCookies(context, accessCookieName, refreshCookieName, accessTokenResult.Token, accessTokenResult.ExpiresAt,
        refreshTokenResult.RawToken, refreshTokenResult.StoredToken.ExpiresAt, useSecureCookies, cookieSameSiteMode, refreshCookiePath);

    return Results.Created($"/api/users/{user.Id}", new AuthResponse(
        accessTokenResult.Token, accessTokenResult.ExpiresAt, refreshTokenResult.StoredToken.ExpiresAt,
        user.Id, user.Username, user.Email, ToRoleName(user.Role)));
});

app.MapPost("/api/auth/login", async (
    HttpContext context,
    LoginRequest request,
    AppDbContext db,
    PasswordHasher hasher,
    JwtTokenService jwtTokenService,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    var login = request.Login.Trim();
    var loginLower = login.ToLowerInvariant();

    var user = await db.Users
        .AsNoTracking()
        .FirstOrDefaultAsync(u => u.Email == loginLower || u.Username.ToLower() == loginLower, ct);

    if (user is null || !user.IsActive || !hasher.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var accessTokenResult = jwtTokenService.CreateToken(user, ToRoleName(user.Role));
    var refreshTokenResult = refreshTokenService.CreateToken(user.Id, GetRequesterIp(context));

    await SetAuditContextAsync(db, user.Id, GetRequesterIp(context), ct);
    db.RefreshTokens.Add(refreshTokenResult.StoredToken);
    await db.SaveChangesAsync(ct);

    AppendAuthCookies(context, accessCookieName, refreshCookieName, accessTokenResult.Token, accessTokenResult.ExpiresAt,
        refreshTokenResult.RawToken, refreshTokenResult.StoredToken.ExpiresAt, useSecureCookies, cookieSameSiteMode, refreshCookiePath);

    return Results.Ok(new AuthResponse(
        accessTokenResult.Token, accessTokenResult.ExpiresAt, refreshTokenResult.StoredToken.ExpiresAt,
        user.Id, user.Username, user.Email, ToRoleName(user.Role)));
});

app.MapPost("/api/auth/refresh", async (
    HttpContext context,
    AppDbContext db,
    JwtTokenService jwtTokenService,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    if (!context.Request.Cookies.TryGetValue(refreshCookieName, out var rawRefreshToken) ||
        string.IsNullOrWhiteSpace(rawRefreshToken))
    {
        return Results.Unauthorized();
    }

    var refreshTokenHash = refreshTokenService.Hash(rawRefreshToken);
    var nowUtc = DateTimeOffset.UtcNow;
    var currentToken = await db.RefreshTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.TokenHash == refreshTokenHash, ct);

    if (currentToken?.User is null)
    {
        return Results.Unauthorized();
    }

    if (!refreshTokenService.IsActive(currentToken, nowUtc) || !currentToken.User.IsActive)
    {
        return Results.Unauthorized();
    }

    var newAccessToken = jwtTokenService.CreateToken(currentToken.User, ToRoleName(currentToken.User.Role));
    var newRefreshToken = refreshTokenService.CreateToken(currentToken.User.Id, GetRequesterIp(context));

    await SetAuditContextAsync(db, currentToken.User.Id, GetRequesterIp(context), ct);
    var deletedTokens = await db.RefreshTokens
        .Where(t => t.Id == currentToken.Id)
        .ExecuteDeleteAsync(ct);
    if (deletedTokens == 0)
    {
        ClearAuthCookies(context, accessCookieName, refreshCookieName, useSecureCookies, cookieSameSiteMode, refreshCookiePath);
        return Results.Unauthorized();
    }

    db.RefreshTokens.Add(newRefreshToken.StoredToken);
    await db.SaveChangesAsync(ct);

    AppendAuthCookies(context, accessCookieName, refreshCookieName, newAccessToken.Token, newAccessToken.ExpiresAt,
        newRefreshToken.RawToken, newRefreshToken.StoredToken.ExpiresAt, useSecureCookies, cookieSameSiteMode, refreshCookiePath);

    return Results.Ok(new RefreshResponse(newAccessToken.Token, newAccessToken.ExpiresAt, newRefreshToken.StoredToken.ExpiresAt));
});

app.MapPost("/api/auth/logout", async (
    HttpContext context,
    AppDbContext db,
    RefreshTokenService refreshTokenService,
    CancellationToken ct) =>
{
    if (context.Request.Cookies.TryGetValue(refreshCookieName, out var rawRefreshToken) &&
        !string.IsNullOrWhiteSpace(rawRefreshToken))
    {
        var tokenHash = refreshTokenService.Hash(rawRefreshToken);
        var storedToken = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (storedToken is not null)
        {
            await SetAuditContextAsync(db, storedToken.UserId, GetRequesterIp(context), ct);
            db.RefreshTokens.Remove(storedToken);
            await db.SaveChangesAsync(ct);
        }
    }

    ClearAuthCookies(context, accessCookieName, refreshCookieName, useSecureCookies, cookieSameSiteMode, refreshCookiePath);
    return Results.Ok();
});

app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue)
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
    if (user is null || !user.IsActive)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        user.Id,
        user.Username,
        user.Email,
        user.FullName,
        user.IsActive,
        RoleName = ToRoleName(user.Role),
        user.CreatedAt
    });
}).RequireAuthorization();

    }
}
