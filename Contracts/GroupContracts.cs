using System.ComponentModel.DataAnnotations;

namespace KeepWalletAPI.Contracts;

public record CreateGroupRequest(
    [Required, StringLength(100)] string Name,
    [StringLength(50)] string? IconKey = null,
    [StringLength(10)] string? Color = null
);

public record UpdateGroupRequest(
    [Required, StringLength(100)] string Name,
    [StringLength(50)] string? IconKey = null,
    [StringLength(10)] string? Color = null
);

public record AddGroupMemberRequest(
    [Required, StringLength(100)] string LoginOrEmail,
    [Required, StringLength(20)] string RoleName
);

public record UpdateGroupMemberRoleRequest(
    [Required, StringLength(20)] string RoleName
);

public record TransferGroupOwnerRequest(
    [Required] Guid NewOwnerUserId
);

public record ShareResourceWithGroupRequest(
    Guid? GroupId
);

public record ReplaceResourceGroupsRequest(
    IReadOnlyList<Guid> GroupIds
);

public record GroupResponse(
    Guid Id,
    string Name,
    string IconKey,
    string? Color,
    string RoleName,
    DateTimeOffset CreatedAt,
    int MemberCount,
    string? OwnerDisplay = null
);

public record GroupMemberResponse(
    Guid GroupId,
    Guid UserId,
    string Username,
    string? FullName,
    string RoleName,
    DateTimeOffset JoinedAt
);
