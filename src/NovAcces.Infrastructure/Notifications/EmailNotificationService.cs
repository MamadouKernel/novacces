using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using QRCoder;

namespace NovAcces.Infrastructure.Notifications;

/// <summary>
/// Envoi du QR d'invitation (REQ-F-03) et des notifications hôte, par email
/// uniquement (décision M. Kodjo du 01/08/2026 — WhatsApp abandonné, voir
/// docs/accord-commercial.md et docs/scenarios-fonctionnels.md §1). Le
/// téléphone du visiteur reste collecté (contact utile pour la sûreté) mais
/// n'est plus un canal de délivrance du QR.
///
/// PngByteQRCode (et non le rendu System.Drawing de QRCoder) est utilisé
/// volontairement : l'hébergement cible est un VPS Linux (Contabo), où
/// System.Drawing.Common nécessite libgdiplus et n'est pas garanti disponible.
/// </summary>
public sealed class EmailNotificationService : INotificationService
{
    private readonly SmtpNotificationOptions _smtp;
    private readonly NotificationBrandingOptions _branding;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(
        IOptions<SmtpNotificationOptions> smtp,
        IOptions<NotificationBrandingOptions> branding,
        ILogger<EmailNotificationService> logger)
    {
        _smtp = smtp.Value;
        _branding = branding.Value;
        _logger = logger;
    }

    public async Task SendVisitInvitationAsync(VisitInvitationNotification notification, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(notification.VisitorEmail))
        {
            _logger.LogWarning(
                "Aucune adresse email pour la visite {VisitId} — QR non transmis automatiquement.",
                notification.VisitId);
            return;
        }

        var qrPng = GenerateQrPng(notification.SignedQrPayload);

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

        _logger.LogInformation("QR envoyé par email pour la visite {VisitId}.", notification.VisitId);
    }

    private static byte[] GenerateQrPng(string signedPayload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(signedPayload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(20);
    }

    /// <summary>
    /// Notification de l'HÔTE (§1.3, §1.6, §2, §7). Canal : EMAIL uniquement.
    ///
    /// Choix assumé : l'hôte est un utilisateur interne, son email est
    /// obligatoire et unique dans le magasin d'identité — c'est donc un canal
    /// toujours disponible.
    ///
    /// Best-effort de bout en bout : aucune exception ne remonte. Un scan est
    /// déjà journalisé et validé quand cette méthode est appelée ; une panne
    /// SMTP ne doit surtout pas le remettre en cause.
    /// </summary>
    public async Task NotifyHostAsync(HostEventNotification notification, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(notification.Host.Email))
        {
            _logger.LogDebug(
                "Notification hôte ignorée pour la visite {VisitId} : aucune adresse email connue.",
                notification.VisitId);
            return;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_smtp.FromAddress, _smtp.FromDisplayName),
                Subject = HostEventMessage.Subject(notification, _branding),
                Body = HostEventMessage.PlainText(notification, _branding),
                IsBodyHtml = false,
            };
            message.To.Add(notification.Host.Email!);

            using var client = new SmtpClient(_smtp.Host, _smtp.Port)
            {
                EnableSsl = _smtp.EnableSsl,
                Credentials = new NetworkCredential(_smtp.Username, _smtp.Password)
            };

            ct.ThrowIfCancellationRequested();
            await client.SendMailAsync(message);

            _logger.LogInformation(
                "Hôte notifié ({Kind}) pour la visite {VisitId}.", notification.Kind, notification.VisitId);
        }
        catch (Exception ex)
        {
            // Minimisation : on journalise l'identifiant opaque de la visite,
            // jamais le nom du visiteur ni l'adresse de l'hôte.
            _logger.LogWarning(ex,
                "Échec de la notification de l'hôte ({Kind}) pour la visite {VisitId}.",
                notification.Kind, notification.VisitId);
        }
    }

    /// <summary>
    /// Best-effort strict : l'appelant (endpoint /forgot-password) renvoie
    /// toujours le même message générique, que l'envoi réussisse ou non — une
    /// exception ici ne doit jamais se propager (elle distinguerait, par son
    /// délai ou son échec, un compte existant d'un compte inexistant).
    /// </summary>
    public async Task SendPasswordResetAsync(PasswordResetNotification notification, CancellationToken ct)
    {
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_smtp.FromAddress, _smtp.FromDisplayName),
                Subject = PasswordResetMessage.Subject(_branding),
                Body = PasswordResetMessage.PlainText(notification, _branding),
                IsBodyHtml = false,
            };
            message.To.Add(notification.Email);

            using var client = new SmtpClient(_smtp.Host, _smtp.Port)
            {
                EnableSsl = _smtp.EnableSsl,
                Credentials = new NetworkCredential(_smtp.Username, _smtp.Password)
            };

            ct.ThrowIfCancellationRequested();
            await client.SendMailAsync(message);

            _logger.LogInformation("Lien de réinitialisation envoyé.");
        }
        catch (Exception ex)
        {
            // Minimisation : jamais l'email en clair dans les logs.
            _logger.LogWarning(ex, "Échec de l'envoi du lien de réinitialisation.");
        }
    }
}
