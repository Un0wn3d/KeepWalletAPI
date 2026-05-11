namespace KeepWalletAPI.Models;

public class Budget
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? GroupId { get; set; }
    public int CategoryId { get; set; }
    public decimal Amount { get; set; }
    public TimeSpan? BudgetPeriod { get; set; }
    public DateOnly StartDate { get; set; }
    public bool IsActive { get; set; } = true;

    public User? User { get; set; }
    public Group? Group { get; set; }
    public Category? Category { get; set; }
}
