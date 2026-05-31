namespace KeepWalletAPI.Models;

public class Budget
{
    public int Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? GroupId { get; set; }
    public int CategoryId { get; set; }
    public decimal Amount { get; set; }

    public BankAccount? Account { get; set; }
    public Group? Group { get; set; }
    public Category? Category { get; set; }
}
