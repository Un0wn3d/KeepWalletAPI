using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record CreateScheduledPaymentRequest(
    [Required, StringLength(200)] string Name,
    TimeSpan RepeatInterval,
    DateTimeOffset NextDueDate,
    bool IsActive
);

public record UpdateScheduledPaymentRequest(
    [Required, StringLength(200)] string Name,
    TimeSpan RepeatInterval,
    DateTimeOffset NextDueDate,
    bool IsActive
);

public record ScheduledPaymentResponse(
    int Id,
    string Name,
    TimeSpan RepeatInterval,
    DateTimeOffset NextDueDate,
    bool IsActive
);
