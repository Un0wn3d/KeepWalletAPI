namespace KeepWalletAPI.Models;

public class SavingItem
{
    public int Id { get; set; }
    public int SavingId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public short? Priority { get; set; }
    public bool IsPurchased { get; set; }

    public Saving? Saving { get; set; }
}
