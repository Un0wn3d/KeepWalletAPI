namespace KeepWalletAPI.Models;

public enum UserRole
{
    Admin,
    User
}

public class User
{
    public Guid Id { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
