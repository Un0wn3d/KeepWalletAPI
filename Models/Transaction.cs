namespace KeepWalletAPI.Models;

public class Transaction
{
    public int Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? GroupId { get; set; }
    public int CategoryId { get; set; }
    public int? SavingId { get; set; }
    public int? RecurringPaymentId { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset TransactionDate { get; set; }

    public BankAccount? Account { get; set; }
    public Group? Group { get; set; }
    public Saving? Saving { get; set; }
}
