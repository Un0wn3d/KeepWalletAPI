namespace KeepWalletAPI.Models;

public class PopularCategoryLast30Days
{
    public Guid UserId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public CategoryType CategoryType { get; set; }
    public long TransactionsCount { get; set; }
    public decimal TotalAmount { get; set; }
}
