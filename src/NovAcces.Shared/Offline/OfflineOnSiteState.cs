using NovAcces.Shared.Dtos;

namespace NovAcces.Shared.Offline;

/// <summary>
/// Reconstruit, hors ligne, l'ensemble des visiteurs considérés « sur site ».
/// Pur et déterministe : c'est l'état LOCAL que l'anti-rejeu du mode dégradé
/// confronte au sens du poste (cf. <see cref="OfflineScanEvaluator"/>).
///
/// Deux sources combinées :
///  1. l'INSTANTANÉ serveur au moment de l'émission de la liste signée
///     (<see cref="OfflineListItem.IsOnSite"/>) — pour ne pas « oublier » un
///     visiteur entré EN LIGNE juste avant la coupure ;
///  2. les scans réalisés localement DEPUIS (entrées accordées ⇒ sur site,
///     sorties effectives ⇒ plus sur site), rejoués dans l'ordre chronologique.
///
/// À la resynchronisation, le registre central reste l'autorité : cet état
/// local n'est qu'une approximation de sûreté pendant la coupure.
/// </summary>
public static class OfflineOnSiteState
{
    public static HashSet<Guid> Compute(
        IEnumerable<OfflineListItem> snapshot, IEnumerable<OfflineScanDto> localScans)
    {
        var onSite = new HashSet<Guid>();

        foreach (var e in snapshot)
            if (e.IsOnSite)
                onSite.Add(e.VisitToken);

        foreach (var s in localScans.OrderBy(s => s.OccurredAt))
        {
            var isExit = string.Equals(s.Direction, "Exit", StringComparison.OrdinalIgnoreCase);
            if (isExit)
            {
                if (s.WasGranted) onSite.Remove(s.VisitToken); // sortie effective
            }
            else
            {
                if (s.WasGranted) onSite.Add(s.VisitToken); // entrée accordée
            }
        }

        return onSite;
    }
}
