using KeepWalletAPI.Middleware;

namespace KeepWalletAPI.Extensions;

public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseErrorHandlingMiddleware(this IApplicationBuilder app) =>
        app.UseMiddleware<ErrorHandlingMiddleware>();
}
