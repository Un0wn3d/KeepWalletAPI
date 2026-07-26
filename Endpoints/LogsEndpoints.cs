using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class LogsEndpoints
{
    internal static void MapLogsEndpoints(this WebApplication app)
    {
app.MapGet("/api/logs/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var isAdmin = await db.Users
        .AsNoTracking()
        .AnyAsync(u => u.Id == userId.Value && u.Role == UserRole.Admin && u.IsActive, ct);

    var logsQuery = db.AuditLogs
        .AsNoTracking()
        .AsQueryable();

    if (!isAdmin)
    {
        var groupUserIds = await db.GroupMembers
            .AsNoTracking()
            .Where(m => db.GroupMembers.Any(my => my.GroupId == m.GroupId && my.UserId == userId.Value))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);

        if (!groupUserIds.Contains(userId.Value))
        {
            groupUserIds.Add(userId.Value);
        }

        logsQuery = logsQuery.Where(l => l.UserId != null && groupUserIds.Contains(l.UserId.Value));
    }

    var logs = await logsQuery
        .GroupJoin(db.Users,
            l => l.UserId,
            u => u.Id,
            (log, users) => new { log, user = users.FirstOrDefault() })
        .OrderByDescending(x => x.log.CreatedAt)
        .Select(x => new AuditLogResponse(
            x.log.Id,
            x.log.UserId,
            x.user == null ? null : x.user.Username,
            x.log.Action,
            x.log.Details,
            x.log.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(logs);
}).RequireAuthorization();

    }
}
