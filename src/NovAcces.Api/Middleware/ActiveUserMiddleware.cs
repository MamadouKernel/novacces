using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using NovAcces.Infrastructure.Identity;

namespace NovAcces.Api.Middleware;

/// <summary>
/// Révoque immédiatement les JWT encore valides après un self-delete logique.
/// Les terminaux API key et les jetons de poste n'ont pas de NameIdentifier
/// Identity et sont donc laissés à leurs propres mécanismes de révocation.
/// </summary>
public sealed class ActiveUserMiddleware
{
    private readonly RequestDelegate _next;

    public ActiveUserMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> users)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub"), out var userId))
        {
            var user = await users.FindByIdAsync(userId.ToString());
            if (user is null || user.IsDeactivated)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Session invalide ou compte désactivé." });
                return;
            }
        }

        await _next(context);
    }
}

public static class ActiveUserMiddlewareExtensions
{
    public static IApplicationBuilder UseActiveUser(this IApplicationBuilder app)
        => app.UseMiddleware<ActiveUserMiddleware>();
}
