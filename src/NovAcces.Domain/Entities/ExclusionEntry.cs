namespace NovAcces.Domain.Entities;

/// <summary>
/// Entrée de la liste d'exclusion d'un site (REQ-F-11). Une personne figurant
/// ici se voit refuser toute nouvelle demande de visite. Le motif est réservé à
/// la sûreté (moindre privilège) : l'agent ne reçoit qu'un refus générique.
///
/// Stockée par tenant (schéma du site) : la liste d'un site n'est jamais visible
/// d'un autre. La correspondance PAR DÉFAUT se fait sur un nom NORMALISÉ (casse
/// et accents neutralisés) pour éviter les contournements triviaux — mais deux
/// personnes distinctes peuvent porter rigoureusement le même nom (homonymes,
/// discuté et validé le 09/08/2026). L'email, quand la sûreté le connaît, permet
/// de PRÉCISER une entrée pour ne viser QUE cette personne (voir <see cref="Email"/>
/// et <see cref="ExclusionMatchKey"/>) — jamais l'inverse : une entrée sans email
/// reste le filet large par défaut, un email absent côté visiteur ne fait jamais
/// disparaître une exclusion qui, elle, n'en porte pas.
/// </summary>
public sealed class ExclusionEntry
{
    public Guid Id { get; private set; }

    /// <summary>Nom tel que saisi (affiché à la sûreté).</summary>
    public string DisplayName { get; private set; } = default!;

    /// <summary>Nom normalisé (minuscule, sans accents) servant à la comparaison.</summary>
    public string NormalizedName { get; private set; } = default!;

    /// <summary>Email tel que saisi (affiché à la sûreté), ou null si l'entrée ne précise pas.</summary>
    public string? Email { get; private set; }

    /// <summary>
    /// Email normalisé (minuscule) servant à la comparaison, ou null. Quand
    /// renseigné, une visite ne correspond à CETTE entrée que si son propre
    /// email correspond aussi — voir <see cref="ExclusionMatchKey.Matches"/>.
    /// </summary>
    public string? NormalizedEmail { get; private set; }

    /// <summary>Motif de l'exclusion — visible de la sûreté uniquement.</summary>
    public string Reason { get; private set; } = default!;

    /// <summary>Identité de l'agent de sûreté qui a ajouté l'entrée.</summary>
    public string AddedBy { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    private ExclusionEntry() { } // EF Core

    public static ExclusionEntry Create(
        string displayName, string reason, string addedBy, DateTimeOffset now, string? email = null)
    {
        return new ExclusionEntry
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName.Trim(),
            NormalizedName = Normalize(displayName),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            NormalizedEmail = NormalizeEmail(email),
            Reason = reason.Trim(),
            AddedBy = addedBy,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Normalise un nom pour la comparaison : suppression des espaces de bord,
    /// passage en minuscules, et retrait des signes diacritiques (accents).
    /// </summary>
    public static string Normalize(string name)
    {
        var trimmed = (name ?? string.Empty).Trim().ToLowerInvariant();
        var decomposed = trimmed.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Normalise un email pour la comparaison : espaces de bord retirés,
    /// minuscules. Contrairement à <see cref="Normalize"/>, aucun retrait
    /// d'accents (non pertinent pour une adresse email). Retourne null pour
    /// une entrée vide/absente — distinct d'une chaîne vide, pour que
    /// "aucun email renseigné" reste sans ambiguïté possible avec un email
    /// littéralement vide.
    /// </summary>
    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}

/// <summary>
/// Forme comparable d'une entrée d'exclusion (nom + email déjà normalisés),
/// utilisée pour vérifier un LOT de visiteurs sans un aller-retour par visite
/// (liste hors ligne signée, vue « toutes les demandes » du dashboard sûreté).
/// </summary>
public sealed record ExclusionMatchKey(string NormalizedName, string? NormalizedEmail)
{
    /// <summary>
    /// Vrai si ce visiteur (nom + email déjà normalisés par l'appelant, voir
    /// <see cref="ExclusionEntry.Normalize"/>/<see cref="ExclusionEntry.NormalizeEmail"/>)
    /// correspond à CETTE entrée. Sans email sur l'entrée : le nom seul suffit
    /// (filet large, comportement historique — REQ-F-11). Avec email sur
    /// l'entrée : les DEUX doivent correspondre — précision explicitement
    /// choisie par la sûreté pour ne pas confondre un homonyme. Un visiteur
    /// sans email ne "débloque" jamais une entrée précisée par email : il ne
    /// correspond simplement pas à CETTE entrée (il peut toujours en
    /// correspondre une autre, sans email, si elle existe).
    /// </summary>
    public bool Matches(string normalizedVisitorName, string? normalizedVisitorEmail) =>
        NormalizedName == normalizedVisitorName
        && (NormalizedEmail is null || NormalizedEmail == normalizedVisitorEmail);

    /// <summary>Vrai si AU MOINS une des clés fournies correspond à ce visiteur.</summary>
    public static bool AnyMatches(
        IEnumerable<ExclusionMatchKey> keys, string visitorName, string? visitorEmail)
    {
        var normalizedName = ExclusionEntry.Normalize(visitorName);
        var normalizedEmail = ExclusionEntry.NormalizeEmail(visitorEmail);
        return keys.Any(k => k.Matches(normalizedName, normalizedEmail));
    }
}
