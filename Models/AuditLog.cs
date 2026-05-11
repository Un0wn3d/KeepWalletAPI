namespace KeepWalletAPI.Models;

public class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? Details { get; set; }
    public string? Device { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User? User { get; set; }
}
