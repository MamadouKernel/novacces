namespace NovAcces.Application.Abstractions;

/// <summary>
/// Signale au canal Admin global (voir ScanEventsHub.GlobalGroup) qu'une
/// entité gérée depuis la console (site, agent, terminal, compte) vient de
/// changer, pour que les tableaux ouverts se rafraîchissent sans que
/// l'opérateur ait à recharger la page. Best-effort — jamais dans le chemin
/// critique d'une opération d'administration.
/// </summary>
public interface IAdminActivityBroadcaster
{
    Task NotifyEntityChangedAsync(string kind, CancellationToken ct);
}
