using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class GroupsEndpoints
{
    internal static void MapGroupsEndpoints(this WebApplication app)
    {
app.MapGet("/api/groups", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var groupRows = await db.GroupMembers
        .AsNoTracking()
        .Where(m => m.UserId == userId.Value)
        .Join(db.Groups,
            m => m.GroupId,
            g => g.Id,
            (m, g) => new { Member = m, Group = g })
        .GroupJoin(db.GroupMembers,
            x => x.Group.Id,
            member => member.GroupId,
            (x, members) => new
            {
                x.Group.Id,
                x.Group.Name,
                x.Group.IconKey,
                x.Group.Color,
                x.Group.CreatedAt,
                x.Member.Role,
                MemberCount = members.Count(),
                OwnerDisplay = members
                    .Where(member => member.Role == UserGroupRole.Owner)
                    .Join(db.Users,
                        member => member.UserId,
                        user => user.Id,
                        (member, user) => user.Username)
                    .FirstOrDefault()
            })
        .OrderBy(g => g.Name)
        .Select(g => new GroupResponse(
            g.Id,
            g.Name,
            g.IconKey ?? "other",
            g.Color,
            g.Role == UserGroupRole.Member ? "member" : g.Role == UserGroupRole.Viewer ? "viewer" : "owner",
            g.CreatedAt,
            g.MemberCount,
            g.OwnerDisplay))
        .ToListAsync(ct);

    var groups = groupRows
        .Select(g => g with { IconKey = NormalizeGroupIconKey(g.IconKey), Color = NormalizeColor(g.Color) })
        .ToList();

    return Results.Ok(groups);
}).RequireAuthorization();

app.MapPost("/api/groups", async (CreateGroupRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var group = new Group
    {
        Name = request.Name.Trim(),
        IconKey = NormalizeGroupIconKey(request.IconKey),
        Color = NormalizeColor(request.Color)
    };

    if (string.IsNullOrWhiteSpace(group.Name))
    {
        return Results.BadRequest(new { message = "Group name is required." });
    }

    db.Groups.Add(group);
    await db.SaveChangesAsync(ct);

    var member = new GroupMember
    {
        GroupId = group.Id,
        UserId = userId.Value,
        Role = UserGroupRole.Owner
    };

    db.GroupMembers.Add(member);
    await db.SaveChangesAsync(ct);

    var creator = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
    var ownerDisplay = creator?.Username;

    return Results.Created($"/api/groups/{group.Id}", new GroupResponse(group.Id, group.Name, NormalizeGroupIconKey(group.IconKey), NormalizeColor(group.Color), "owner", group.CreatedAt, 1, ownerDisplay));
}).RequireAuthorization();

app.MapPatch("/api/groups/{id:guid}", async (Guid id, UpdateGroupRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    var name = request.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new { message = "Group name is required." });
    }

    var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
    if (group is null) return Results.NotFound();

    group.Name = name;
    if (request.IconKey is not null)
    {
        group.IconKey = NormalizeGroupIconKey(request.IconKey);
    }
    if (request.Color is not null)
    {
        group.Color = NormalizeColor(request.Color);
    }
    await db.SaveChangesAsync(ct);

    var memberCount = await db.GroupMembers.CountAsync(m => m.GroupId == id, ct);
    var ownerDisplay = await db.GroupMembers
        .Where(m => m.GroupId == id && m.Role == UserGroupRole.Owner)
        .Join(db.Users,
            member => member.UserId,
            user => user.Id,
            (member, user) => user.Username)
        .FirstOrDefaultAsync(ct);

    return Results.Ok(new GroupResponse(group.Id, group.Name, NormalizeGroupIconKey(group.IconKey), NormalizeColor(group.Color), "owner", group.CreatedAt, memberCount, ownerDisplay));
}).RequireAuthorization();

app.MapGet("/api/groups/{id:guid}/members", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var isMember = await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (!isMember) return Results.NotFound();

    var members = await db.GroupMembers
        .AsNoTracking()
        .Where(m => m.GroupId == id)
        .Join(db.Users,
            m => m.UserId,
            u => u.Id,
            (m, u) => new { Member = m, User = u })
        .OrderBy(x => x.User.Username)
        .Select(x => new GroupMemberResponse(
                x.Member.GroupId,
                x.User.Id,
                x.User.Username,
                x.User.FullName,
                x.Member.Role == UserGroupRole.Member ? "member" : x.Member.Role == UserGroupRole.Viewer ? "viewer" : "owner",
                x.Member.JoinedAt))
        .ToListAsync(ct);

    return Results.Ok(members);
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/members", async (Guid id, AddGroupMemberRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role is UserGroupRole.Viewer) return Results.NotFound();

    var login = request.LoginOrEmail.Trim();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == login || u.Email == login, ct);
    if (user is null) return Results.NotFound();

    var existing = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == user.Id, ct);
    var role = ParseGroupRole(request.RoleName);

    if (existing is null)
    {
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = id,
            UserId = user.Id,
            Role = role,
            JoinedAt = DateTimeOffset.UtcNow
        });
    }
    else
    {
        existing.Role = role;
    }

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/groups/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
    if (group is null) return Results.NotFound();

    var members = await db.GroupMembers.Where(m => m.GroupId == id).ToListAsync(ct);
    db.GroupMembers.RemoveRange(members);
    db.Groups.Remove(group);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPatch("/api/groups/{id:guid}/members/{memberUserId:guid}", async (Guid id, Guid memberUserId, UpdateGroupMemberRoleRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();

    var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == memberUserId, ct);
    if (member is null) return Results.NotFound();
    if (member.Role == UserGroupRole.Owner) return Results.BadRequest(new { message = "Transfer ownership to change the owner role." });

    var role = ParseGroupRole(request.RoleName);
    if (role == UserGroupRole.Owner) return Results.BadRequest(new { message = "Use owner transfer endpoint." });

    member.Role = role;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/transfer-owner", async (Guid id, TransferGroupOwnerRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null || requester.Role != UserGroupRole.Owner) return Results.NotFound();
    if (request.NewOwnerUserId == userId.Value) return Results.BadRequest(new { message = "You are already the owner." });

    var newOwner = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == request.NewOwnerUserId, ct);
    if (newOwner is null) return Results.NotFound();

    requester.Role = UserGroupRole.Member;
    newOwner.Role = UserGroupRole.Owner;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/groups/{id:guid}/members/{memberUserId:guid}", async (Guid id, Guid memberUserId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var requester = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId.Value, ct);
    if (requester is null) return Results.NotFound();

    var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == memberUserId, ct);
    if (member is null) return Results.NotFound();

    if (memberUserId == userId.Value)
    {
        if (member.Role == UserGroupRole.Owner)
        {
            return Results.BadRequest(new { message = "Transfer ownership before leaving the group." });
        }

        await DeleteOwnedGroupAccessAsync(db, id, userId.Value, ct);

        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    if (requester.Role != UserGroupRole.Owner) return Results.NotFound();
    if (member.Role == UserGroupRole.Owner) return Results.BadRequest(new { message = "Transfer ownership before removing the owner." });

    await DeleteOwnedGroupAccessAsync(db, id, memberUserId, ct);

    db.GroupMembers.Remove(member);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

    }
}
