namespace NovAcces.Application.Abstractions;

/// <summary>
/// Supervision des dépassements de durée (§7 des scénarios) : un passage balaie
/// tous les sites, évalue les visiteurs encore présents au-delà de leur durée
/// prévue, et déclenche l'escalade (alerte niveau 1 → rappels → niveau 3 =
/// événement de sécurité). Jamais bloquant. Appelé périodiquement par un service
/// de fond ; isolé ici pour être testable indépendamment du minuteur.
/// </summary>
public interface IOverstayScanner
{
    Task ScanOnceAsync(CancellationToken ct);
}
