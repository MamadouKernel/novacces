namespace NovAcces.Application.Abstractions;

/// <summary>
/// Retrouve les coordonnées de l'HÔTE d'une visite pour pouvoir le prévenir
/// (arrivée, départ, anomalie, dépassement — §1, §2 et §7 des scénarios
/// fonctionnels). Le domaine ne connaît de l'hôte que son identifiant opaque
/// (<c>Visit.HostUserId</c>) ; la résolution vers un email ou un téléphone vit
/// dans l'Infrastructure, aux côtés du magasin d'identité.
/// </summary>
public interface IHostDirectory
{
    /// <summary>
    /// Coordonnées de l'hôte, ou null s'il est introuvable (compte supprimé,
    /// visite créée par un compte de service). Un hôte introuvable n'est jamais
    /// une erreur : la notification est simplement abandonnée.
    /// </summary>
    Task<HostContact?> FindAsync(string hostUserId, CancellationToken ct);

    /// <summary>
    /// Même résolution que <see cref="FindAsync"/>, mais pour plusieurs hôtes
    /// en une seule requête — évite un aller-retour par ligne quand une page
    /// affiche plusieurs demandes d'hôtes différents (dashboard sûreté :
    /// journal, liste des demandes). Les identifiants introuvables sont
    /// simplement absents du résultat, jamais une erreur.
    /// </summary>
    Task<IReadOnlyDictionary<string, HostContact>> FindManyAsync(
        IReadOnlyCollection<string> hostUserIds, CancellationToken ct);
}

/// <summary>Coordonnées d'un hôte destinataire d'une notification.</summary>
public sealed record HostContact(string DisplayName, string? Email, string? Phone);
