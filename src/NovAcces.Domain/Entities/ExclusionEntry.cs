namespace NovAcces.Domain.Entities;

/// <summary>
/// Entrée de la liste d'exclusion d'un site (REQ-F-11). Une personne figurant
/// ici se voit refuser toute nouvelle demande de visite. Le motif est réservé à
/// la sûreté (moindre privilège) : l'agent ne reçoit qu'un refus générique.
///
/// Stockée par tenant (schéma du site) : la liste d'un site n'est jamais visible
/// d'un autre. La correspondance se fait sur un nom NORMALISÉ (casse et accents
/// neutralisés) pour éviter les contournements triviaux.
/// </summary>
public sealed class ExclusionEntry
{
    public Guid Id { get; private set; }

    /// <summary>Nom tel que saisi (affiché à la sûreté).</summary>
    public string DisplayName { get; private set; } = default!;

    /// <summary>Nom normalisé (minuscule, sans accents) servant à la comparaison.</summary>
    public string NormalizedName { get; private set; } = default!;

    /// <summary>Motif de l'exclusion — visible de la sûreté uniquement.</summary>
    public string Reason { get; private set; } = default!;

    /// <summary>Identité de l'agent de sûreté qui a ajouté l'entrée.</summary>
    public string AddedBy { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }

    private ExclusionEntry() { } // EF Core

    public static ExclusionEntry Create(string displayName, string reason, string addedBy, DateTimeOffset now)
    {
        return new ExclusionEntry
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName.Trim(),
            NormalizedName = Normalize(displayName),
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
}
