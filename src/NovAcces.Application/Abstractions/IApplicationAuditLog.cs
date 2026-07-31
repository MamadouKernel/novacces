namespace NovAcces.Application.Abstractions;

/// <summary>
/// Journal transversal de toutes les requêtes API. Il complète le journal métier
/// par site (IAdminAuditLog) et conserve la méthode, la route, le résultat et
/// l'acteur résolu par le pipeline d'authentification.
/// </summary>
public interface IApplicationAuditLog
{
    Task RecordAsync(
        string actor,
        string method,
        string path,
        int statusCode,
        string? siteId,
        string? ipAddress,
        CancellationToken ct);
}