namespace NovAcces.Application.Abstractions;

/// <summary>
/// Génère et vérifie le code de secours saisissable manuellement (alternative
/// au QR — voir Visit.AssignManualCode / Visit.Scan). Même principe que la
/// clé API d'un terminal (TerminalDirectory) : le code brut n'est jamais
/// persisté, seule son empreinte l'est.
/// </summary>
public interface IManualCodeService
{
    /// <summary>Génère un nouveau code (ex. "K3XM-7QRT") et son empreinte à persister.</summary>
    (string RawCode, string CodeHash) GenerateCode();

    /// <summary>
    /// Normalise (espaces/tirets retirés, majuscules) puis hache une saisie
    /// utilisateur avec l'algorithme COURANT (PBKDF2, préfixe "v2$"), pour
    /// comparaison avec l'empreinte persistée.
    /// </summary>
    string ComputeHash(string rawCode);

    /// <summary>
    /// Ancien algorithme (SHA-256 nu, sans préfixe) — repli de vérification EN
    /// LIGNE uniquement, pour les codes émis avant le 09/08/2026 (voir
    /// ManualCodeService). Jamais utilisé pour la liste hors ligne.
    /// </summary>
    string ComputeLegacyHash(string rawCode);
}
