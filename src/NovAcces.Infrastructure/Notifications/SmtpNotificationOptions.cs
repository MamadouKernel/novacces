namespace NovAcces.Infrastructure.Notifications;

/// <summary>Paramètres du canal email utilisé en complément de WhatsApp.</summary>
public sealed class SmtpNotificationOptions
{
    public string Host { get; set; } = default!;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string FromAddress { get; set; } = default!;
    public string FromDisplayName { get; set; } = "NovAcces";
}
