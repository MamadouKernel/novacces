using System.Security.Cryptography;
using System.Text;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Security;

/// <summary>
/// Implémentation du code de secours — même principe que
/// TerminalDirectory.GenerateSecret/ComputeKeyHash (secret aléatoire à haute
/// entropie, seule l'empreinte SHA-256 persistée ; pas de hachage lent/salé,
/// qui n'apporte rien pour un secret déjà aléatoire et coûterait cher à
/// chaque scan).
/// </summary>
public sealed class ManualCodeService : IManualCodeService
{
    // Exclut 0/O et 1/I/L : ambigus à l'oral et à l'écrit — précisément le
    // canal visé (le visiteur relit/relaie ce code de vive voix ou par SMS).
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 8;

    public (string RawCode, string CodeHash) GenerateCode()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(0, Alphabet.Length)];

        var raw = new string(chars);
        var formatted = $"{raw[..4]}-{raw[4..]}";
        return (formatted, ComputeHash(formatted));
    }

    public string ComputeHash(string rawCode)
    {
        var normalized = Normalize(rawCode);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }

    private static string Normalize(string rawCode) =>
        (rawCode ?? string.Empty).Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
}
