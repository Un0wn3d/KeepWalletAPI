namespace KeepWalletAPI.Models;

public class GroupResourceAccess
{
    public Guid GroupId { get; set; }
    public Guid? AccountId { get; set; }
    public int? SavingId { get; set; }
    public int? TransactionId { get; set; }
    public Guid? SharedBy { get; set; }

    public Group? Group { get; set; }
    public BankAccount? Account { get; set; }
    public Saving? Saving { get; set; }
    public Transaction? Transaction { get; set; }
    public User? SharedByUser { get; set; }
}
