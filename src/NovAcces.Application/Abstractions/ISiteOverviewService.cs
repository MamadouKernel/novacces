namespace NovAcces.Application.Abstractions;

/// <summary>
/// Vue consolidée multi-sites (§10) : pour chaque site provisionné, l'activité
/// du jour et les visiteurs présents. Transverse aux tenants (réservé à l'Admin).
/// </summary>
public interface ISiteOverviewService
{
    Task<IReadOnlyList<SiteOverview>> GetAsync(CancellationToken ct);
}

/// <summary>
/// Activité et état des postes d'un site (§10).
/// </summary>
/// <param name="TerminalsEnrolled">Terminaux actifs et enrôlés autorisés sur ce site.</param>
/// <param name="TerminalsActive">
/// Terminaux ayant scanné récemment. Il n'existe pas de « connexion permanente »
/// d'un poste au serveur : l'activité récente au journal est le seul indicateur
/// honnête de vie d'un terminal. Un poste sans scan depuis la fenêtre est donc
/// « silencieux » — ce qui peut vouloir dire hors ligne, éteint, ou simplement
/// sans visiteur.
/// </param>
/// <param name="DegradedScansToday">
/// Scans du jour validés hors ligne. C'est le signal qu'un site a connu une
/// coupure réseau, même s'il est de nouveau joignable maintenant.
/// </param>
public sealed record SiteOverview(
    string SiteId,
    int OnSite,
    int ScansToday,
    int TerminalsEnrolled,
    int TerminalsActive,
    int DegradedScansToday);
