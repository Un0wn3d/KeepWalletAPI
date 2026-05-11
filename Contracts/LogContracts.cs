namespace KeepWalletAPI.Contracts;

public record AuditLogResponse(
    long Id,
    Guid? UserId,
    string? Username,
    string Action,
    string? EntityType,
    string? Details,
    string? Device,
    DateTimeOffset CreatedAt
);
