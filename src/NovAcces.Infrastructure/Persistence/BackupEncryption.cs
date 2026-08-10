using System.Security.Cryptography;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// Chiffrement au repos des sauvegardes complètes (§7.4 — une sauvegarde en
/// clair contient les données de TOUS les clients de Sigasécurité). AES-256-GCM
/// (chiffrement authentifié — une seule primitive, pas de HMAC séparé à
/// orchestrer correctement) via <c>System.Security.Cryptography</c> natif,
/// cohérent avec le choix ES256 du reste du projet : zéro dépendance
/// cryptographique tierce à auditer. Clé dérivée par PBKDF2 (sel aléatoire
/// PAR sauvegarde, embarqué en clair dans le fichier — un sel n'est pas un
/// secret, seule la passphrase l'est, jamais journalisée ni persistée).
/// </summary>
internal static class BackupEncryption
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;
    private static readonly byte[] Magic = "NVBK1"u8.ToArray();

    public static void EncryptFile(string plainPath, string encryptedPath, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(passphrase, salt);

        var plaintext = File.ReadAllBytes(plainPath);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        using var output = new FileStream(encryptedPath, FileMode.Create, FileAccess.Write);
        output.Write(Magic);
        output.Write(salt);
        output.Write(nonce);
        output.Write(tag);
        output.Write(ciphertext);
    }

    public static void DecryptFile(string encryptedPath, string plainPath, string passphrase)
    {
        var data = File.ReadAllBytes(encryptedPath);
        if (data.Length < Magic.Length + SaltSize + NonceSize + TagSize)
            throw new DatabaseBackupFailedException("Fichier de sauvegarde chiffrée tronqué ou invalide.");

        var offset = 0;
        if (!data.AsSpan(offset, Magic.Length).SequenceEqual(Magic))
            throw new DatabaseBackupFailedException("Format de sauvegarde chiffrée invalide (en-tête inattendu).");
        offset += Magic.Length;

        var salt = data.AsSpan(offset, SaltSize).ToArray(); offset += SaltSize;
        var nonce = data.AsSpan(offset, NonceSize).ToArray(); offset += NonceSize;
        var tag = data.AsSpan(offset, TagSize).ToArray(); offset += TagSize;
        var ciphertext = data.AsSpan(offset).ToArray();

        var key = DeriveKey(passphrase, salt);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            throw new DatabaseBackupFailedException(
                "Échec du déchiffrement de la sauvegarde : passphrase incorrecte ou fichier corrompu/altéré.");
        }

        File.WriteAllBytes(plainPath, plaintext);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
}
