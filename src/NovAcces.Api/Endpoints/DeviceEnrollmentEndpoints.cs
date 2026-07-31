using System.Security.Cryptography;
using Microsoft.AspNetCore.RateLimiting;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Activation publique limitée par un ticket QR. Le ticket est le seul secret
/// transitoire présenté ici ; l'API remet ensuite une nouvelle clé de terminal.
/// </summary>
public static class DeviceEnrollmentEndpoints
{
    public static void MapDeviceEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/device-enrollments/activate", async (
            DeviceEnrollmentRequestDto request,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Ticket)
                || string.IsNullOrWhiteSpace(request.DeviceInstanceId)
                || string.IsNullOrWhiteSpace(request.DevicePublicKeyPem))
                return Results.BadRequest(new { error = "Ticket, identifiant du device et clé publique requis." });

            if (!Guid.TryParse(request.DeviceInstanceId, out _))
                return Results.BadRequest(new { error = "Identifiant du device invalide." });

            if (!IsValidDevicePublicKey(request.DevicePublicKeyPem))
                return Results.BadRequest(new { error = "Clé publique du device invalide (ES256/P-256 attendue)." });

            var activation = await terminals.ActivateAsync(
                request.Ticket.Trim(), request.DeviceInstanceId.Trim(), request.DevicePublicKeyPem.Trim(), ct);
            if (activation is null)
                return Results.Json(new { error = "Ticket invalide, expiré, déjà utilisé ou terminal révoqué." },
                    statusCode: StatusCodes.Status410Gone);

            // L'activation est une action de sécurité : elle est écrite dans le
            // journal append-only de chaque site autorisé par le terminal.
            foreach (var siteId in activation.SiteIds)
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
                var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
                await audit.RecordAsync(
                    AdminAuditAction.TerminalActivated,
                    $"device:{request.DeviceInstanceId.Trim()}",
                    activation.TerminalId.ToString(),
                    $"Terminal « {activation.Label} » activé par QR pour le device {request.DeviceInstanceId.Trim()}.",
                    ct);
            }

            return Results.Ok(new DeviceEnrollmentActivationDto(
                activation.TerminalId, activation.Label, activation.SiteIds,
                activation.ApiKey, activation.EnrolledAt));
        })
        .AllowAnonymous()
        .RequireRateLimiting("sensitive")
        .WithTags("Device enrollment")
        .WithName("ActivateDeviceEnrollment")
        .WithSummary("Active un terminal avec un ticket QR temporaire à usage unique.");
    }

    private static bool IsValidDevicePublicKey(string pem)
    {
        if (pem.Length > 12_000 || !pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
            return false;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return ecdsa.KeySize == 256;
        }
        catch
        {
            return false;
        }
    }
}