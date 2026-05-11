using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record CreateSavingRequest(
    [Required, StringLength(200)] string Name,
    [Range(typeof(decimal), "0.01", "999999999")] decimal? TargetAmount,
    [Range(typeof(decimal), "0", "999999999")] decimal CurrentAmount,
    DateOnly? Deadline
);

public record UpdateSavingRequest(
    [Required, StringLength(200)] string Name,
    [Range(typeof(decimal), "0.01", "999999999")] decimal? TargetAmount,
    [Range(typeof(decimal), "0", "999999999")] decimal CurrentAmount,
    DateOnly? Deadline,
    bool IsCompleted
);

public record SavingResponse(
    int Id,
    Guid UserId,
    Guid? GroupId,
    string Name,
    decimal? TargetAmount,
    decimal CurrentAmount,
    DateOnly? Deadline,
    bool IsCompleted,
    DateTimeOffset CreatedAt
);

public record CreateSavingItemRequest(
    [Required, StringLength(255)] string Name,
    [Range(typeof(decimal), "0", "999999999")] decimal Price,
    [Range(0, short.MaxValue)] short? Priority,
    bool IsPurchased
);

public record UpdateSavingItemRequest(
    [Required, StringLength(255)] string Name,
    [Range(typeof(decimal), "0", "999999999")] decimal Price,
    [Range(0, short.MaxValue)] short? Priority,
    bool IsPurchased
);

public record SavingItemResponse(
    int Id,
    int SavingId,
    string Name,
    decimal Price,
    short? Priority,
    bool IsPurchased
);
