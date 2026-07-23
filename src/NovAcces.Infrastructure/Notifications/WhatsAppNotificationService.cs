using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using QRCoder;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Envoi du QR d'invitation (REQ-F-03) : WhatsApp Business Platform en
/// canal principal, email en repli automatique si WhatsApp échoue ou si le
/// visiteur n'a pas de téléphone. PngByteQRCode (et non le rendu
/// System.Drawing de QRCoder) est utilisé volontairement : l'hébergement
/// cible est un VPS Linux (Contabo), où System.Drawing.Common nécessite
/// libgdiplus et n'est pas garanti disponible.
/// </summary>
public sealed class WhatsAppNotificationService : INotificationService
{
    private readonly HttpClient _http;
    private readonly WhatsAppCloudApiOptions _whatsApp;
    private readonly SmtpNotificationOptions _smtp;
    private readonly ILogger<WhatsAppNotificationService> _logger;

    public WhatsAppNotificationService(
        HttpClient http,
        IOptions<WhatsAppCloudApiOptions> whatsApp,
        IOptions<SmtpNotificationOptions> smtp,
        ILogger<WhatsAppNotificationService> logger)
    {
        _http = http;
        _whatsApp = whatsApp.Value;
        _smtp = smtp.Value;
        _logger = logger;
    }

    public async Task SendVisitInvitationAsync(VisitInvitationNotification notification, CancellationToken ct)
    {
        var qrPng = GenerateQrPng(notification.SignedQrPayload);

        if (!string.IsNullOrWhiteSpace(notification.VisitorPhone))
        {
            try
            {
                await SendViaWhatsAppAsync(notification, qrPng, ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Échec de l'envoi WhatsApp du QR pour {VisitorName}, repli sur email.",
                    notification.VisitorName);
            }
        }

        if (!string.IsNullOrWhiteSpace(notification.VisitorEmail))
        {
            await SendViaEmailAsync(notification, qrPng, ct);
            return;
        }

        _logger.LogWarning(
            "Aucun canal de notification n'a abouti pour {VisitorName} (téléphone et/ou email manquant ou en échec) — QR non transmis automatiquement.",
            notification.VisitorName);
    }

    private static byte[] GenerateQrPng(string signedPayload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(signedPayload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(20);
    }

    private async Task SendViaWhatsAppAsync(VisitInvitationNotification notification, byte[] qrPng, CancellationToken ct)
    {
        var mediaId = await UploadMediaAsync(qrPng, ct);

        var payload = new
        {
            messaging_product = "whatsapp",
            to = NormalizePhone(notification.VisitorPhone!),
            type = "template",
            template = new
            {
                name = _whatsApp.TemplateName,
                language = new { code = _whatsApp.TemplateLanguageCode },
                components = new object[]
                {
                    new
                    {
                        type = "header",
                        parameters = new object[] { new { type = "image", image = new { id = mediaId } } }
                    },
                    new
                    {
                        type = "body",
                        parameters = new object[]
                        {
                            new { type = "text", text = notification.VisitorName },
                            new { type = "text", text = FormatSchedule(notification.ScheduledAt) }
                        }
                    }
                }
            }
        };

        using var response = await _http.PostAsJsonAsync($"{_whatsApp.PhoneNumberId}/messages", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> UploadMediaAsync(byte[] qrPng, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(qrPng);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "file", "qr-invitation.png");
        content.Add(new StringContent("whatsapp"), "messaging_product");

        using var response = await _http.PostAsync($"{_whatsApp.PhoneNumberId}/media", content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<WhatsAppMediaUploadResponse>(cancellationToken: ct);
        return body?.Id ?? throw new InvalidOperationException("Réponse d'upload média WhatsApp sans identifiant.");
    }

    private async Task SendViaEmailAsync(VisitInvitationNotification notification, byte[] qrPng, CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_smtp.FromAddress, _smtp.FromDisplayName),
            Subject = "Votre QR Code d'accès NovAcces",
            Body = BuildEmailBody(notification),
        };
        message.To.Add(notification.VisitorEmail!);

        using var qrStream = new MemoryStream(qrPng);
        message.Attachments.Add(new Attachment(qrStream, "qr-invitation.png", "image/png"));

        using var client = new SmtpClient(_smtp.Host, _smtp.Port)
        {
            EnableSsl = _smtp.EnableSsl,
            Credentials = new NetworkCredential(_smtp.Username, _smtp.Password)
        };

        ct.ThrowIfCancellationRequested();
        await client.SendMailAsync(message);
    }

    private static string BuildEmailBody(VisitInvitationNotification notification) =>
        notification.ScheduledAt is { } scheduledAt
            ? $"Bonjour {notification.VisitorName},\n\nVoici votre QR Code d'accès pour le rendez-vous du {scheduledAt:dd/MM/yyyy HH:mm}. Présentez-le au poste de contrôle.\n\nCe QR est valable jusqu'au {notification.ExpiresAt:dd/MM/yyyy HH:mm}."
            : $"Bonjour {notification.VisitorName},\n\nVoici votre QR Code d'accès, valable jusqu'au {notification.ExpiresAt:dd/MM/yyyy HH:mm}. Présentez-le au poste de contrôle à chaque passage.";

    private static string FormatSchedule(DateTimeOffset? scheduledAt) =>
        scheduledAt is { } s ? s.ToString("dd/MM/yyyy HH:mm") : "accès valable 30 jours";

    private static string NormalizePhone(string phone) => phone.Replace(" ", "").Replace("+", "");

    private sealed record WhatsAppMediaUploadResponse(string Id);
}
