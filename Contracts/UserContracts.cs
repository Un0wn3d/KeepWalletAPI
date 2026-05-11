using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record CreateUserRequest(
    [Required, RegularExpression("admin|user", ErrorMessage = "Role must be 'admin' or 'user'.")] string Role,
    [Required, StringLength(100)] string Username,
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, StringLength(255)] string Password,
    [StringLength(255)] string? FullName
);

public record UpdateUserRequest(
    [RegularExpression("admin|user", ErrorMessage = "Role must be 'admin' or 'user'.")] string? Role = null,
    [StringLength(100)] string? Username = null,
    [EmailAddress, StringLength(255)] string? Email = null,
    [StringLength(255)] string? Password = null,
    [StringLength(255)] string? FullName = null,
    bool? IsActive = null
);

public record UserResponse(
    Guid Id,
    string RoleName,
    string Username,
    string Email,
    string? FullName,
    bool IsActive,
    DateTimeOffset CreatedAt
);
