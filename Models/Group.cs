namespace KeepWalletAPI.Models;

public enum UserGroupRole
{
    Owner,
    Member,
    Viewer
}

public class Group
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? IconKey { get; set; } = "other";
    public DateTimeOffset CreatedAt { get; set; }
}

public class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public UserGroupRole Role { get; set; } = UserGroupRole.Owner;
    public DateTimeOffset JoinedAt { get; set; }

    public Group? Group { get; set; }
    public User? User { get; set; }
}
