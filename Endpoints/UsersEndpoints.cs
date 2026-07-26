using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using static ApiHelpers;
internal static class UsersEndpoints
{
    internal static void MapUsersEndpoints(this WebApplication app)
    {
app.MapGet("/api/users", async (AppDbContext db, CancellationToken ct) =>
{
    var users = await db.Users
        .AsNoTracking()
        .Select(u => new UserResponse(u.Id, u.Role == UserRole.Admin ? "admin" : "user", u.Username, u.Email, u.FullName, u.IsActive, u.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/users/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
{
    var user = await db.Users
        .AsNoTracking()
        .Where(u => u.Id == id)
        .Select(u => new UserResponse(u.Id, u.Role == UserRole.Admin ? "admin" : "user", u.Username, u.Email, u.FullName, u.IsActive, u.CreatedAt))
        .FirstOrDefaultAsync(ct);

    return user is null ? Results.NotFound() : Results.Ok(user);
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/users/search", async (string? q, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var userId = GetUserIdFromPrincipal(principal);
    if (!userId.HasValue) return Results.Unauthorized();

    var query = (q ?? string.Empty).Trim().ToLowerInvariant();
    var users = await db.Users
        .AsNoTracking()
        .Where(u => u.Id != userId.Value && u.IsActive)
        .Where(u => string.IsNullOrWhiteSpace(query) ||
            u.Username.ToLower().Contains(query) ||
            u.Email.ToLower().Contains(query) ||
            (u.FullName != null && u.FullName.ToLower().Contains(query)))
        .OrderBy(u => u.Username)
        .Take(25)
        .Select(u => new UserResponse(u.Id, u.Role == UserRole.Admin ? "admin" : "user", u.Username, u.Email, u.FullName, u.IsActive, u.CreatedAt))
        .ToListAsync(ct);

    return Results.Ok(users);
}).RequireAuthorization();

app.MapPost("/api/users", async (CreateUserRequest request, AppDbContext db, PasswordHasher hasher, CancellationToken ct) =>
{
    var role = NormalizeRole(request.Role);
    if (role is null)
    {
        return Results.BadRequest(new { message = "Role must be 'admin' or 'user'." });
    }

    var user = new User
    {
        Role = role.Value,
        Username = request.Username.Trim(),
        Email = request.Email.Trim().ToLowerInvariant(),
        PasswordHash = hasher.Hash(request.Password),
        FullName = request.FullName?.Trim()
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/users/{user.Id}", new UserResponse(
        user.Id, ToRoleName(user.Role), user.Username, user.Email, user.FullName, user.IsActive, user.CreatedAt));
}).RequireAuthorization("AdminOnly");

app.MapPatch("/api/users/{id:guid}", async (Guid id, UpdateUserRequest request, ClaimsPrincipal principal, AppDbContext db, PasswordHasher hasher, CancellationToken ct) =>
{
    var actorUserId = GetUserIdFromPrincipal(principal);
    if (!actorUserId.HasValue) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    if (request.Role is not null)
    {
        var role = NormalizeRole(request.Role);
        if (role is null)
        {
            return Results.BadRequest(new { message = "Role must be 'admin' or 'user'." });
        }

        user.Role = role.Value;
    }

    if (request.Username is not null) user.Username = request.Username.Trim();
    if (request.Email is not null) user.Email = request.Email.Trim().ToLowerInvariant();
    if (request.Password is not null) user.PasswordHash = hasher.Hash(request.Password);
    if (request.FullName is not null) user.FullName = request.FullName.Trim();
    if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;
    if (request.CreatedAt.HasValue) user.CreatedAt = request.CreatedAt.Value;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/users/{id:guid}", async (Guid id, UpdateUserRequest request, ClaimsPrincipal principal, AppDbContext db, PasswordHasher hasher, CancellationToken ct) =>
{
    var actorUserId = GetUserIdFromPrincipal(principal);
    if (!actorUserId.HasValue) return Results.Unauthorized();

    if (request.Role is null ||
        string.IsNullOrWhiteSpace(request.Username) ||
        string.IsNullOrWhiteSpace(request.Email) ||
        string.IsNullOrWhiteSpace(request.Password) ||
        !request.IsActive.HasValue)
    {
        return Results.BadRequest(new { message = "Role, Username, Email, Password and IsActive are required for PUT." });
    }

    var role = NormalizeRole(request.Role);
    if (role is null)
    {
        return Results.BadRequest(new { message = "Role must be 'admin' or 'user'." });
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    user.Role = role.Value;
    user.Username = request.Username.Trim();
    user.Email = request.Email.Trim().ToLowerInvariant();
    user.PasswordHash = hasher.Hash(request.Password);
    user.FullName = request.FullName?.Trim();
    user.IsActive = request.IsActive.Value;
    if (request.CreatedAt.HasValue) user.CreatedAt = request.CreatedAt.Value;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

app.MapDelete("/api/users/{id:guid}", async (Guid id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
{
    var actorUserId = GetUserIdFromPrincipal(principal);
    if (!actorUserId.HasValue) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null)
    {
        return Results.NotFound();
    }

    var plannedTransactions = await db.Transactions
        .Include(t => t.Account)
        .Where(t => t.RecurringPaymentId != null && t.Account != null && t.Account.UserId == id)
        .ToListAsync(ct);
    var plannedPaymentIds = plannedTransactions
        .Select(t => t.RecurringPaymentId)
        .Where(paymentId => paymentId.HasValue)
        .Select(paymentId => paymentId!.Value)
        .Distinct()
        .ToArray();
    db.Transactions.RemoveRange(plannedTransactions);

    if (plannedPaymentIds.Length > 0)
    {
        var plannedPayments = await db.ScheduledPayments
            .Where(payment => plannedPaymentIds.Contains(payment.Id))
            .ToListAsync(ct);
        db.ScheduledPayments.RemoveRange(plannedPayments);
    }

    var groupMemberships = await db.GroupMembers
        .Where(member => member.UserId == id)
        .ToListAsync(ct);
    db.GroupMembers.RemoveRange(groupMemberships);

    user.IsActive = false;
    user.Username = $"deleted-{id:N}";
    user.Email = $"deleted-{id:N}@deleted.local";
    user.FullName = null;
    user.PasswordHash = string.Empty;

    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

    }
}
