using KeepWalletAPI.Models;
using System.Security.Cryptography;
using System.Text;

namespace KeepWalletAPI.Security;

public sealed class RefreshTokenService(IConfiguration configuration)
{
    public (string RawToken, RefreshToken StoredToken) CreateToken(Guid userId, Guid jwtId, string? createdByIp)
    {
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var now = DateTimeOffset.UtcNow;
        var expiresDays = int.TryParse(configuration["Jwt:RefreshTokenExpiresDays"], out var parsedDays) ? parsedDays : 30;

        var storedToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(rawToken),
            JwtId = jwtId,
            ExpiresAt = now.AddDays(expiresDays),
            CreatedByIp = createdByIp,
            CreatedAt = now,
            UpdatedAt = now
        };

        return (rawToken, storedToken);
    }

    public string Hash(string token)
    {
        var input = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash);
    }

    public bool IsActive(RefreshToken token, DateTimeOffset nowUtc) =>
        token.RevokedAt is null && token.ExpiresAt > nowUtc;
}
