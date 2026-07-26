using static ApiHelpers;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Data;
using KeepWalletAPI.Extensions;
using KeepWalletAPI.Models;
using KeepWalletAPI.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions =>
        {
            npgsqlOptions.MapEnum<UserRole>("user_role");
            npgsqlOptions.MapEnum<UserGroupRole>("user_group_role");
            npgsqlOptions.MapEnum<CategoryType>("category_type");
        }));
builder.Services.AddScoped<PasswordHasher>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<RefreshTokenService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer is missing.");
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience is missing.");

var accessCookieName = builder.Configuration["Auth:AccessCookieName"] ?? "access_token";
var refreshCookieName = builder.Configuration["Auth:RefreshCookieName"] ?? "refresh_token";
var refreshCookiePath = builder.Configuration["Auth:RefreshCookiePath"] ?? "/api/auth";
var cookieSameSiteMode = ParseSameSiteMode(builder.Configuration["Auth:CookieSameSite"]);
var useSecureCookies = bool.TryParse(builder.Configuration["Auth:UseSecureCookies"], out var secureCookiesParsed)
    ? secureCookiesParsed
    : !builder.Environment.IsDevelopment();

if (cookieSameSiteMode == SameSiteMode.None && !useSecureCookies)
{
    throw new InvalidOperationException("SameSite=None requires secure cookies.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue(accessCookieName, out var cookieToken))
                {
                    context.Token = cookieToken;
                    return Task.CompletedTask;
                }

                if (string.IsNullOrWhiteSpace(context.Token) &&
                    context.Request.Headers.TryGetValue("Authorization", out StringValues authHeader))
                {
                    var token = authHeader.ToString().Trim();
                    while (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        token = token["Bearer ".Length..].Trim();
                    }

                    context.Token = token.Trim('"');
                }

                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { message = "Unauthorized" });
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
            .RequireAssertion(context =>
                context.User.Claims.Any(c =>
                    (c.Type == ClaimTypes.Role || c.Type == "role") &&
                    string.Equals(c.Value, "admin", StringComparison.OrdinalIgnoreCase))));
});

await EnsureDatabaseAndSchemaAsync(builder.Configuration, builder.Environment, CancellationToken.None);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseErrorHandlingMiddleware();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    var userId = GetUserIdFromPrincipal(context.User);
    if (!userId.HasValue)
    {
        await next();
        return;
    }

    var db = context.RequestServices.GetRequiredService<AppDbContext>();
    if (!db.Database.IsRelational())
    {
        await next();
        return;
    }

    await db.Database.OpenConnectionAsync(context.RequestAborted);
    try
    {
        await SetAuditContextAsync(db, userId.Value, GetRequesterIp(context), context.RequestAborted);
        await next();
    }
    finally
    {
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.current_user_id', '', false), set_config('app.device', '', false);");
        }

        await db.Database.CloseConnectionAsync();
    }
});

app.MapAppSettingsEndpoints();
app.MapCategoriesEndpoints();
app.MapBudgetsEndpoints();
app.MapUsersEndpoints();
app.MapGroupsEndpoints();
app.MapLogsEndpoints();
app.MapAuthEndpoints(accessCookieName, refreshCookieName, refreshCookiePath, cookieSameSiteMode, useSecureCookies);
app.MapBankAccountsEndpoints();
app.MapSavingsEndpoints();
app.MapPlannedTransactionsEndpoints();
app.MapTransactionsEndpoints();

app.Run();

