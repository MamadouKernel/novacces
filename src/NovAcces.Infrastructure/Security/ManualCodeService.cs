using System.Security.Cryptography;
using System.Text;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Security;

/// <summary>
/// Implémentation du code de secours.
///
/// Empreinte DURCIE (PBKDF2-HMAC-SHA256, préfixe "v2$") depuis le 09/08/2026 :
/// initialement un simple SHA-256 non salé (même raisonnement que
/// TerminalDirectory.GenerateSecret/ComputeKeyHash — secret déjà aléatoire, un
/// hachage lent n'apportait rien tant que seule une vérification EN LIGNE,
/// limitée en débit, y avait accès). Ce postulat change dès lors que le code
/// devient vérifiable HORS LIGNE (empreinte incluse dans la liste du jour mise
/// en cache sur le terminal, voir AgentContractEndpoints./offline-list) : un
/// terminal volé exposerait alors une cible de craquage local, sans aucune
/// limite de débit. Un SHA-256 nu sur 8 caractères (32^8 ≈ 2^40) s'y casse
/// entièrement en 1-2 minutes sur du matériel courant.
///
/// Double mitigation : longueur du code portée à 12 caractères (32^12 ≈ 2^60,
/// déjà très solide à elle seule) ET facteur de travail PBKDF2 (défense en
/// profondeur, ralentit toute tentative indépendamment de l'entropie du code).
/// PBKDF2 plutôt qu'Argon2/scrypt : nativement disponible via
/// System.Security.Cryptography (zéro dépendance tierce à auditer, même
/// raisonnement que le choix ES256 pour la signature des QR) ; le nombre
/// d'itérations reste modéré (pas le niveau "mot de passe humain") car
/// l'entrée est déjà uniformément aléatoire sur 2^60, pas un secret choisi par
/// un humain à faible entropie — et le calcul doit rester rapide sur un
/// téléphone d'entrée de gamme (voir la contrepartie JS, computeManualCodeHash
/// dans sigasacces-mobile/src/lib/crypto.ts, qui DOIT rester synchronisée :
/// même pepper, mêmes itérations, même préfixe).
///
/// Migration : les codes émis AVANT ce changement portent une empreinte
/// SHA-256 nue (64 caractères hex, sans préfixe) — ComputeLegacyHash() permet
/// à ScanManualCodeHandler de les vérifier encore EN LIGNE (jamais hors ligne :
/// AgentContractEndpoints ne met dans la liste signée que les empreintes "v2$").
/// Purge possible de ce repli après l'expiration de tous les codes antérieurs
/// (30 jours maximum, mode ThirtyDays).
/// </summary>
public sealed class ManualCodeService : IManualCodeService
{
    // Exclut 0/O et 1/I/L : ambigus à l'oral et à l'écrit — précisément le
    // canal visé (le visiteur relit/relaie ce code de vive voix ou par SMS).
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 12;

    private const string HashPrefix = "v2$";

    // Domain-separation uniquement (comme TerminalDirectory.KeyPepper) : pas un
    // secret, l'entropie vient du code lui-même. DOIT être identique à la
    // constante JS miroir côté mobile.
    private const string Pepper = "SigasAcces_ManualCode_Pepper_v2_PBKDF2";
    private const int Iterations = 50_000;
    private const int KeyLengthBytes = 32;

    public (string RawCode, string CodeHash) GenerateCode()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(0, Alphabet.Length)];

        var raw = new string(chars);
        var formatted = string.Join('-', raw.Chunk(4).Select(c => new string(c)));
        return (formatted, ComputeHash(formatted));
    }

    public string ComputeHash(string rawCode)
    {
        var normalized = Normalize(rawCode);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized), Encoding.UTF8.GetBytes(Pepper),
            Iterations, HashAlgorithmName.SHA256, KeyLengthBytes);
        return HashPrefix + Convert.ToHexString(derived);
    }

    /// <summary>Ancien algorithme (SHA-256 nu) — vérification EN LIGNE uniquement, voir la note de classe.</summary>
    public string ComputeLegacyHash(string rawCode)
    {
        var normalized = Normalize(rawCode);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static string Normalize(string rawCode) =>
        (rawCode ?? string.Empty).Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
}
