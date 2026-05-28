namespace KeepWalletAPI.Models;

public class Saving
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? GroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public string Currency { get; set; } = "UAH";
    public string? IconKey { get; set; }
    public string? Color { get; set; }
    public DateOnly? Deadline { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User? User { get; set; }
}
