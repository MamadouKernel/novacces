using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using QRCoder;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Envoi du QR d'invitation (REQ-F-03) : WhatsApp Business Platform en
/// canal principal, email en repli automatique si WhatsApp échoue ou si le
/// visiteur n'a pas de téléphone. Les messages (email et légende WhatsApp)
/// sont rédigés dans <see cref="InvitationMessage"/>.
///
/// PngByteQRCode (et non le rendu System.Drawing de QRCoder) est utilisé
/// volontairement : l'hébergement cible est un VPS Linux (Contabo), où
/// System.Drawing.Common nécessite libgdiplus et n'est pas garanti disponible.
/// </summary>
public sealed class WhatsAppNotificationService : INotificationService
{
    private readonly HttpClient _http;
    private readonly WhatsAppCloudApiOptions _whatsApp;
    private readonly SmtpNotificationOptions _smtp;
    private readonly NotificationBrandingOptions _branding;
    private readonly ILogger<WhatsAppNotificationService> _logger;

    public WhatsAppNotificationService(
        HttpClient http,
        IOptions<WhatsAppCloudApiOptions> whatsApp,
        IOptions<SmtpNotificationOptions> smtp,
        IOptions<NotificationBrandingOptions> branding,
        ILogger<WhatsAppNotificationService> logger)
    {
        _http = http;
        _whatsApp = whatsApp.Value;
        _smtp = smtp.Value;
        _branding = branding.Value;
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
                    "Échec de l'envoi WhatsApp du QR pour la visite {VisitId}, repli sur email.",
                    notification.VisitId);
            }
        }

        if (!string.IsNullOrWhiteSpace(notification.VisitorEmail))
        {
            await SendViaEmailAsync(notification, qrPng, ct);
            return;
        }

        _logger.LogWarning(
            "Aucun canal de notification n'a abouti pour la visite {VisitId} (téléphone et/ou email manquant ou en échec) — QR non transmis automatiquement.",
            notification.VisitId);
    }

    private static byte[] GenerateQrPng(string signedPayload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(signedPayload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(20);
    }

    // ---------------------------------------------------------------- WhatsApp

    private async Task SendViaWhatsAppAsync(VisitInvitationNotification notification, byte[] qrPng, CancellationToken ct)
    {
        var mediaId = await UploadMediaAsync(qrPng, ct);
        var to = NormalizePhone(notification.VisitorPhone!);

        var isTemplate = string.Equals(_whatsApp.SendMode, "Template", StringComparison.OrdinalIgnoreCase);
        object payload = isTemplate
            ? BuildTemplatePayload(notification, to, mediaId)
            : BuildImagePayload(notification, to, mediaId);

        using var response = await _http.PostAsJsonAsync($"{_whatsApp.PhoneNumberId}/messages", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    // Envoi du QR en image avec une légende rédigée (conforme à l'accord :
    // « QR envoyé en image dans la conversation »).
    private object BuildImagePayload(VisitInvitationNotification notification, string to, string mediaId) => new
    {
        messaging_product = "whatsapp",
        to,
        type = "image",
        image = new
        {
            id = mediaId,
            caption = InvitationMessage.WhatsAppCaption(notification, _branding)
        }
    };

    // Message basé sur un template Meta pré-approuvé (premier contact hors
    // fenêtre de 24 h). Header image (QR) + corps à deux paramètres :
    // {{1}} = nom du visiteur, {{2}} = créneau/validité.
    private object BuildTemplatePayload(VisitInvitationNotification notification, string to, string mediaId) => new
    {
        messaging_product = "whatsapp",
        to,
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

    // ------------------------------------------------------------------- Email

    private async Task SendViaEmailAsync(VisitInvitationNotification notification, byte[] qrPng, CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_smtp.FromAddress, _smtp.FromDisplayName),
            Subject = InvitationMessage.Subject(notification),
            Body = InvitationMessage.PlainText(notification, _branding),
            IsBodyHtml = false,
        };
        message.To.Add(notification.VisitorEmail!);

        // Vue HTML avec le QR intégré (cid:qr) — s'affiche directement dans le
        // corps du message ; la vue texte reste le repli des clients sans HTML.
        var html = InvitationMessage.Html(notification, _branding);
        var htmlView = AlternateView.CreateAlternateViewFromString(html, null, MediaTypeNames.Text.Html);
        var qrResource = new LinkedResource(new MemoryStream(qrPng), "image/png")
        {
            ContentId = "qr",
            TransferEncoding = TransferEncoding.Base64,
            ContentType = { Name = "qr-invitation.png" }
        };
        htmlView.LinkedResources.Add(qrResource);
        message.AlternateViews.Add(htmlView);

        // Le QR également en pièce jointe téléchargeable.
        message.Attachments.Add(new Attachment(new MemoryStream(qrPng), "qr-invitation.png", "image/png"));

        using var client = new SmtpClient(_smtp.Host, _smtp.Port)
        {
            EnableSsl = _smtp.EnableSsl,
            Credentials = new NetworkCredential(_smtp.Username, _smtp.Password)
        };

        ct.ThrowIfCancellationRequested();
        await client.SendMailAsync(message);
    }

    private static string FormatSchedule(DateTimeOffset? scheduledAt) =>
        scheduledAt is { } s ? s.ToString("dd/MM/yyyy HH:mm") : "accès valable 30 jours ouvrés";

    private static string NormalizePhone(string phone) => phone.Replace(" ", "").Replace("+", "");

    private sealed record WhatsAppMediaUploadResponse(string Id);
}
