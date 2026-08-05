using System.Security.Cryptography;
using System.Text;
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

            // PREUVE DE POSSESSION : le device doit démontrer qu'il détient la
            // clé privée correspondant à la clé publique qu'il enregistre. Sans
            // ce contrôle, la clé publique stockée sur le terminal n'attestait
            // de rien — un ticket intercepté (QR photographié, capture d'écran
            // transmise) permettait à un appareil tiers de s'enrôler à la place
            // du bon et de repartir avec la clé API du terminal.
            if (!HasValidProofOfPossession(
                    request.DevicePublicKeyPem, request.Ticket.Trim(),
                    request.DeviceInstanceId.Trim(), request.ProofSignature))
                return Results.BadRequest(new
                {
                    error = "Preuve de possession de la clé du device absente ou invalide."
                });

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
        .WithSummary("Active un terminal avec un ticket QR temporaire à usage unique.")
        .WithDescription(
            "Preuve de possession (ProofSignature) : signe {ticket}|{deviceInstanceId} "
            + "(UTF-8, séparateur '|' littéral) en ECDSA P-256/SHA-256 (ES256), signature "
            + "au format IEEE P1363 (r||s, PAS DER), encodée en Base64URL. Clé publique "
            + "attendue au format PEM SPKI (\"BEGIN PUBLIC KEY\").")
        .Produces<DeviceEnrollmentActivationDto>(StatusCodes.Status200OK)
        .Produces<ErrorResponseDto>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponseDto>(StatusCodes.Status410Gone);
    }

    /// <summary>
    /// Vérifie que « {ticket}|{deviceInstanceId} » a bien été signé par la clé
    /// privée associée à la clé publique présentée. Le message lie la preuve AU
    /// ticket (à usage unique et à durée de vie courte) ET à l'installation :
    /// une signature capturée ne vaut donc rien pour un autre enrôlement.
    /// </summary>
    private static bool HasValidProofOfPossession(
        string devicePublicKeyPem, string ticket, string deviceInstanceId, string proofSignature)
    {
        if (string.IsNullOrWhiteSpace(proofSignature))
            return false;

        try
        {
            var signature = Base64UrlDecode(proofSignature);
            var message = Encoding.UTF8.GetBytes($"{ticket}|{deviceInstanceId}");

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(devicePublicKeyPem);
            return ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            // Signature malformée = preuve absente. Comme pour un QR falsifié,
            // c'est un cas nominal de refus, pas une erreur technique.
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var s = text.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
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