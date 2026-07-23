namespace NovAcces.Application.Abstractions;

/// <summary>
/// Abstraction de l'horloge : indispensable pour tester la fenêtre de validité
/// (-20/+15 min) sans dépendre de l'heure système, et pour garantir que
/// TOUTE l'application raisonne sur l'horloge serveur (REQ-SEC-02).
/// </summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
