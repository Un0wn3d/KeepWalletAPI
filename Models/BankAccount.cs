namespace KeepWalletAPI.Models;

public class BankAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = "UAH";
    public decimal Balance { get; set; }
    public bool IsDefault { get; set; }

    public User? User { get; set; }
}
