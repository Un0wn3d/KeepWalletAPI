using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record CreateBankAccountRequest(
    [Required, StringLength(100)] string Name,
    [StringLength(3, MinimumLength = 3)] string Currency,
    [Range(typeof(decimal), "0", "999999999")] decimal Balance,
    bool IsDefault
);

public record UpdateBankAccountRequest(
    [Required, StringLength(100)] string Name,
    [StringLength(3, MinimumLength = 3)] string Currency,
    [Range(typeof(decimal), "0", "999999999")] decimal Balance,
    bool IsDefault
);

public record BankAccountResponse(
    Guid Id,
    Guid UserId,
    Guid? GroupId,
    string Name,
    string Currency,
    decimal Balance,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
