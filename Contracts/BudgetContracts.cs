using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record BudgetResponse(
    int Id,
    Guid UserId,
    Guid? GroupId,
    int CategoryId,
    decimal Amount,
    TimeSpan? BudgetPeriod,
    DateOnly StartDate,
    bool IsActive
);

public record UpsertBudgetRequest(
    Guid? GroupId,
    [Range(typeof(decimal), "0", "999999999")] decimal Amount,
    TimeSpan? BudgetPeriod,
    DateOnly StartDate,
    bool IsActive
);
