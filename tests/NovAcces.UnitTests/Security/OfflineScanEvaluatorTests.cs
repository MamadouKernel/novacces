using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Security;
using NovAcces.Shared.Offline;
using Xunit;

namespace NovAcces.UnitTests.Security;

/// <summary>
/// Décision de scan HORS LIGNE (§6) : reproduit, sans base ni réseau, les
/// verdicts de la maquette en mode dégradé. Les QR et la liste sont signés par
/// le vrai service serveur et vérifiés avec la clé publique.
/// </summary>
public class OfflineScanEvaluatorTests
{
    private sealed class Fixture
    {
        public Es256QrSigningService Server { get; }
        public OfflineQrVerifier Verifier { get; }

        public Fixture()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Server = new Es256QrSigningService(Options.Create(new QrSigningOptions
            {
                PrivateKeyPem = key.ExportECPrivateKeyPem(),
                PublicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
            }));
            Verifier = new OfflineQrVerifier(key.ExportSubjectPublicKeyInfoPem());
        }

        public OfflineListResult SignedList(DateTimeOffset now, DateTimeOffset expiresAt, params OfflineListEntry[] entries)
        {
            var signed = Server.SignDailyOfflineList(entries, now, expiresAt);
            return Verifier.VerifyDailyList(signed, now);
        }
    }

    [Fact]
    public void ValidQr_InList_WithinWindow_IsRecognized()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        var verdict = OfflineScanEvaluator.Evaluate(fx.Verifier, qr, list, now);

        Assert.Equal(OfflineOutcome.Recognized, verdict.Outcome);
        Assert.Equal(token, verdict.VisitToken);
        Assert.False(verdict.IsSecurityEvent);
    }

    [Fact]
    public void TamperedQr_IsInvalidSignature_SecurityEvent()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        const string marker = "\"SignatureB64Url\":\"";
        var i = qr.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var chars = qr.ToCharArray(); chars[i] = chars[i] == 'A' ? 'B' : 'A';

        var verdict = OfflineScanEvaluator.Evaluate(fx.Verifier, new string(chars), list, now);

        Assert.Equal(OfflineOutcome.InvalidSignature, verdict.Outcome);
        Assert.True(verdict.IsSecurityEvent);
    }

    [Fact]
    public void ValidQr_NotInList_IsVerificationImpossible_NotSecurity()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var list = fx.SignedList(now, now.AddHours(4)); // liste vide
        var qr = fx.Server.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(15));

        var verdict = OfflineScanEvaluator.Evaluate(fx.Verifier, qr, list, now);

        Assert.Equal(OfflineOutcome.NotInLocalList, verdict.Outcome);
        Assert.False(verdict.IsSecurityEvent);
    }

    [Fact]
    public void ExpiredList_IsUnavailable()
    {
        var fx = new Fixture();
        var issued = DateTimeOffset.UtcNow.AddHours(-5);
        var list = fx.SignedList(issued, issued.AddHours(4)); // TTL déjà dépassé (now)
        var listNow = fx.Verifier.VerifyDailyList(fx.Server.SignDailyOfflineList(
            Array.Empty<OfflineListEntry>(), issued, issued.AddHours(4)), DateTimeOffset.UtcNow);
        var qr = fx.Server.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        var verdict = OfflineScanEvaluator.Evaluate(fx.Verifier, qr, listNow, DateTimeOffset.UtcNow);

        Assert.Equal(OfflineOutcome.ListUnavailable, verdict.Outcome);
    }

    [Fact]
    public void ExcludedEntry_IsRefusedGenerically()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, true)); // exclu
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        var verdict = OfflineScanEvaluator.Evaluate(fx.Verifier, qr, list, now);

        Assert.Equal(OfflineOutcome.Excluded, verdict.Outcome);
        Assert.DoesNotContain("exclu", verdict.Message, StringComparison.OrdinalIgnoreCase); // motif jamais exposé
    }

    [Fact]
    public void TooEarly_And_TooLate_AreSecurityEvents()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());

        var early = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now.AddHours(1), false));
        var late = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now.AddHours(-1), false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddHours(2));

        Assert.Equal(OfflineOutcome.TooEarly, OfflineScanEvaluator.Evaluate(fx.Verifier, qr, early, now).Outcome);
        Assert.Equal(OfflineOutcome.TooLate, OfflineScanEvaluator.Evaluate(fx.Verifier, qr, late, now).Outcome);
    }

    [Fact]
    public void ThirtyDayMode_OnWeekend_IsNonBusinessDay()
    {
        var fx = new Fixture();
        var saturday = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero); // samedi
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        // Mode 30 jours = ScheduledAt null.
        var list = fx.SignedList(saturday, saturday.AddHours(4), new OfflineListEntry(visitId, token, null, false));
        var qr = fx.Server.SignVisitToken(visitId, token, saturday.AddDays(1));

        var verdict = OfflineScanEvaluator.Evaluate(fx.Verifier, qr, list, saturday);

        Assert.Equal(OfflineOutcome.NonBusinessDay, verdict.Outcome);
        Assert.True(verdict.IsSecurityEvent);
    }
}
