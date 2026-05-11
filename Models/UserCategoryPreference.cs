namespace KeepWalletAPI.Models;

public class UserCategoryPreference
{
    public Guid UserId { get; set; }
    public int CategoryId { get; set; }

    public User? User { get; set; }
    public Category? Category { get; set; }
}
