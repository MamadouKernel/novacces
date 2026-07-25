using System.Globalization;
using System.Text;

namespace NovAcces.Web.Services;

/// <summary>
/// Transforme un nom saisi librement (« Côte d'Ivoire Terminal ») en
/// identifiant technique de site valide (« cote_ivoire_terminal ») : le champ
/// sert de nom de schéma PostgreSQL, d'où la contrainte a-z 0-9 _ (max 40).
/// Rend la saisie facile : l'admin tape le vrai nom, l'app propose le slug.
/// </summary>
public static class SiteSlug
{
    public const int MaxLength = 40;

    public static string From(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        // 1) Retire les accents (é → e) via décomposition Unicode.
        var normalized = input.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(c);
        }

        // 2) Remplace tout caractère non autorisé par « _ », puis compacte.
        var cleaned = new StringBuilder(sb.Length);
        var lastUnderscore = false;
        foreach (var c in sb.ToString().Normalize(NormalizationForm.FormC))
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                cleaned.Append(c);
                lastUnderscore = false;
            }
            else if (!lastUnderscore && cleaned.Length > 0)
            {
                cleaned.Append('_');
                lastUnderscore = true;
            }
        }

        var slug = cleaned.ToString().Trim('_');
        return slug.Length > MaxLength ? slug[..MaxLength].Trim('_') : slug;
    }
}
