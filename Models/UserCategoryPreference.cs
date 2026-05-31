namespace KeepWalletAPI.Models;

public class UserCategoryPreference
{
    public Guid UserId { get; set; }
    public int CategoryId { get; set; }
    public string? IconKey { get; set; }
    public string? Color { get; set; }
    public bool IsActive { get; set; } = true;

    public User? User { get; set; }
    public Category? Category { get; set; }
}
