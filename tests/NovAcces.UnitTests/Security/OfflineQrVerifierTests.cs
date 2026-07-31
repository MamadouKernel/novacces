using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Security;
using NovAcces.Shared.Offline;
using Xunit;

namespace NovAcces.UnitTests.Security;

/// <summary>
/// Compatibilité croisée serveur → agent : ce qui est SIGNÉ par le serveur
/// (Es256QrSigningService, clé privée) doit être VÉRIFIABLE hors ligne par
/// l'agent (OfflineQrVerifier, clé publique seule). C'est la garantie que le
/// mode dégradé fonctionne réellement (§5, §6) — vérification purement
/// mathématique, sans base ni réseau.
/// </summary>
public class OfflineQrVerifierTests
{
    private static (Es256QrSigningService server, string publicPem) CreatePair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = Options.Create(new QrSigningOptions
        {
            PrivateKeyPem = key.ExportECPrivateKeyPem(),
            PublicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
        });
        return (new Es256QrSigningService(options), key.ExportSubjectPublicKeyInfoPem());
    }

    [Fact]
    public void ServerSignedToken_IsVerifiableOffline_WithPublicKeyOnly()
    {
        var (server, publicPem) = CreatePair();
        var visitId = Guid.NewGuid();
        var visitToken = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        var signed = server.SignVisitToken(visitId, visitToken, expiresAt);

        using var offline = new OfflineQrVerifier(publicPem);
        var result = offline.VerifyToken(signed);

        Assert.True(result.IsValid);
        Assert.Equal(visitId, result.VisitId);
        Assert.Equal(visitToken, result.VisitToken);
    }

    [Fact]
    public void TamperedToken_IsRejectedOffline()
    {
        var (server, publicPem) = CreatePair();
        var signed = server.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));

        // On altère le premier caractère de la signature (bits toujours significatifs).
        const string marker = "\"SignatureB64Url\":\"";
        var start = signed.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var chars = signed.ToCharArray();
        chars[start] = chars[start] == 'A' ? 'B' : 'A';

        using var offline = new OfflineQrVerifier(publicPem);
        Assert.False(offline.VerifyToken(new string(chars)).IsValid);
    }

    [Fact]
    public void TokenSignedByAnotherKey_IsRejectedOffline()
    {
        var (server, _) = CreatePair();
        var (_, otherPublicPem) = CreatePair(); // clé publique qui ne correspond pas

        var signed = server.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));

        using var offline = new OfflineQrVerifier(otherPublicPem);
        Assert.False(offline.VerifyToken(signed).IsValid);
    }

    [Fact]
    public void ServerSignedDailyList_IsVerifiableOffline()
    {
        var (server, publicPem) = CreatePair();
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddHours(4);
        var token = Guid.NewGuid();

        var entries = new[] { new OfflineListEntry(Guid.NewGuid(), token, issuedAt, false) };
        var signedList = server.SignDailyOfflineList(entries, issuedAt, expiresAt);

        using var offline = new OfflineQrVerifier(publicPem);
        var result = offline.VerifyDailyList(signedList, issuedAt.AddMinutes(1));

        Assert.True(result.IsValid);
        Assert.False(result.IsExpired);
        Assert.Contains(result.Entries, e => e.VisitToken == token);
    }

    [Fact]
    public void ExpiredDailyList_IsFlaggedExpired()
    {
        var (server, publicPem) = CreatePair();
        var issuedAt = DateTimeOffset.UtcNow.AddHours(-5);
        var expiresAt = issuedAt.AddHours(4); // déjà dépassé

        var signedList = server.SignDailyOfflineList(Array.Empty<OfflineListEntry>(), issuedAt, expiresAt);

        using var offline = new OfflineQrVerifier(publicPem);
        var result = offline.VerifyDailyList(signedList, DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);      // signature valide
        Assert.True(result.IsExpired);    // mais TTL dépassé -> plus de validation locale
    }
}
