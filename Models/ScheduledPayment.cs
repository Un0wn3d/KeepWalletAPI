namespace KeepWalletAPI.Models;

public class ScheduledPayment
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeSpan RepeatInterval { get; set; }
    public DateOnly NextDueDate { get; set; }
    public bool IsActive { get; set; } = true;
}
