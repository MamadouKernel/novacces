using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NovAcces.Infrastructure.Security;
using Xunit;

namespace NovAcces.UnitTests.Security;

public class Es256QrSigningServiceTests
{
    private static Es256QrSigningService CreateService(out string publicPem)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = key.ExportECPrivateKeyPem();
        publicPem = key.ExportSubjectPublicKeyInfoPem();

        var options = Options.Create(new QrSigningOptions
        {
            PrivateKeyPem = privatePem,
            PublicKeyPem = publicPem
        });

        return new Es256QrSigningService(options);
    }

    [Fact]
    public void SignAndVerify_ValidToken_ReturnsOriginalData()
    {
        var service = CreateService(out _);
        var visitId = Guid.NewGuid();
        var visitToken = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        var signed = service.SignVisitToken(visitId, visitToken, expiresAt);
        var result = service.VerifySignedToken(signed);

        Assert.True(result.IsValid);
        Assert.Equal(visitId, result.VisitId);
        Assert.Equal(visitToken, result.VisitToken);
    }

    [Fact]
    public void Verify_TamperedPayload_IsRejected()
    {
        var service = CreateService(out _);
        var signed = service.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));

        // Falsification déterministe : on inverse le PREMIER caractère Base64Url
        // de la signature. Il faut viser le premier (et non le dernier) caractère :
        // une signature ECDSA P-256 fait 64 octets, or 64 mod 3 = 1, donc le
        // dernier groupe Base64 code 1 octet sur 2 caractères et les 4 bits de
        // poids faible du DERNIER caractère sont ignorés au décodage — inverser
        // ce caractère-là peut être un no-op (la signature décodée reste
        // identique), ce qui rendait ce test non déterministe. Le premier
        // caractère d'un champ Base64, lui, porte des bits toujours significatifs.
        const string marker = "\"SignatureB64Url\":\"";
        var sigStart = signed.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var chars = signed.ToCharArray();
        chars[sigStart] = chars[sigStart] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        var result = service.VerifySignedToken(tampered);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Verify_SignedWithDifferentKey_IsRejected()
    {
        var serviceA = CreateService(out _);
        var signedByA = serviceA.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(15));

        var serviceB = CreateService(out _); // clé totalement différente

        var result = serviceB.VerifySignedToken(signedByA);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Verify_MalformedInput_DoesNotThrow_AndIsRejected()
    {
        var service = CreateService(out _);

        var result = service.VerifySignedToken("ceci n'est pas un JSON valide du tout");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void SignAndVerify_DailyOfflineList_RoundTrips()
    {
        var service = CreateService(out _);
        var entries = new[]
        {
            new NovAcces.Application.Abstractions.OfflineListEntry(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, false),
            new NovAcces.Application.Abstractions.OfflineListEntry(Guid.NewGuid(), Guid.NewGuid(), null, true),
        };
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.AddHours(4);

        var signed = service.SignDailyOfflineList(entries, issuedAt, expiresAt);
        var result = service.VerifyDailyOfflineList(signed);

        Assert.True(result.IsValid);
        Assert.False(result.IsExpired);
        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public void VerifyDailyOfflineList_PastExpiry_IsMarkedExpired()
    {
        var service = CreateService(out _);
        var entries = Array.Empty<NovAcces.Application.Abstractions.OfflineListEntry>();
        var issuedAt = DateTimeOffset.UtcNow.AddHours(-6);
        var expiresAt = issuedAt.AddHours(4); // expirée depuis 2h

        var signed = service.SignDailyOfflineList(entries, issuedAt, expiresAt);
        var result = service.VerifyDailyOfflineList(signed);

        Assert.True(result.IsValid); // signature toujours valide
        Assert.True(result.IsExpired); // mais TTL dépassé (REQ-SEC-06.b)
    }
}
