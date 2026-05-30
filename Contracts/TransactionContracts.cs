using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record CreateTransactionRequest(
    Guid AccountId,
    Guid? GroupId,
    int CategoryId,
    int? SavingId,
    int? RecurringPaymentId,
    [Range(typeof(decimal), "0.01", "999999999")] decimal Amount,
    [StringLength(500)] string? Description,
    DateTimeOffset TransactionDate
);

public record UpdateTransactionRequest(
    Guid AccountId,
    Guid? GroupId,
    int CategoryId,
    int? SavingId,
    int? RecurringPaymentId,
    [Range(typeof(decimal), "0.01", "999999999")] decimal Amount,
    [StringLength(500)] string? Description,
    DateTimeOffset TransactionDate
);

public record TransactionResponse(
    int Id,
    Guid AccountId,
    Guid? GroupId,
    string? GroupName,
    string? SharedBy,
    int CategoryId,
    int? SavingId,
    int? RecurringPaymentId,
    decimal Amount,
    string? Description,
    DateTimeOffset TransactionDate
);

public record CreatePlannedTransactionRequest(
    Guid AccountId,
    int CategoryId,
    [Required, StringLength(200)] string Name,
    [Range(typeof(decimal), "0.01", "999999999")] decimal Amount,
    [StringLength(500)] string? Description,
    DateTimeOffset NextDueDate,
    TimeSpan RepeatInterval
);

public record PlannedTransactionResponse(
    int Id,
    Guid AccountId,
    int CategoryId,
    int RecurringPaymentId,
    string Name,
    decimal Amount,
    string? Description,
    DateTimeOffset TransactionDate,
    TimeSpan RepeatInterval,
    DateTimeOffset NextDueDate,
    bool IsActive
);
