using System.Security.Claims;
using NovAcces.Application.Abstractions;

namespace NovAcces.Api.Middleware;

/// <summary>Trace chaque action HTTP de l'application dans le journal global.</summary>
public sealed class ApplicationAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApplicationAuditMiddleware> _logger;

    public ApplicationAuditMiddleware(RequestDelegate next, ILogger<ApplicationAuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IApplicationAuditLog audit,
        ICurrentTenant tenant)
    {
        try
        {
            await _next(context);
        }
        finally
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
            {
                var actor = context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? context.User?.FindFirstValue(ClaimTypes.Name)
                    ?? "anonymous";

                try
                {
                    await audit.RecordAsync(
                        actor,
                        context.Request.Method,
                        context.Request.Path.Value ?? "/",
                        context.Response.StatusCode,
                        tenant.IsResolved ? tenant.SiteId : null,
                        context.Connection.RemoteIpAddress?.ToString(),
                        context.RequestAborted);
                }
                catch (Exception ex)
                {
                    // La journalisation ne transforme jamais une réponse métier
                    // en erreur : l'échec est remonté aux logs d'exploitation.
                    _logger.LogError(ex, "Échec d'écriture du journal global pour {Method} {Path}.",
                        context.Request.Method, context.Request.Path);
                }
            }
        }
    }
}

public static class ApplicationAuditMiddlewareExtensions
{
    public static IApplicationBuilder UseApplicationAudit(this IApplicationBuilder app)
        => app.UseMiddleware<ApplicationAuditMiddleware>();
}