using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Security;

/// <summary>Horloge système réelle (UTC). L'horloge serveur fait foi (REQ-SEC-02) —
/// jamais l'heure envoyée par un client, mobile ou web.</summary>
public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
