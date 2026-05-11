using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record LoginRequest(
    [Required, StringLength(255)] string Login,
    [Required, StringLength(255)] string Password
);

public record RegisterRequest(
    [Required, StringLength(100)] string Username,
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, StringLength(255)] string Password,
    [StringLength(255)] string? FullName
);

public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid UserId,
    string Username,
    string Email,
    string? RoleName
);

public record RefreshResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt
);
