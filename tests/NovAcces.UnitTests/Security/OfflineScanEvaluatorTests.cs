using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Security;
using NovAcces.Shared.Offline;
using Xunit;

namespace NovAcces.UnitTests.Security;

/// <summary>
/// Décision de scan HORS LIGNE (§6) : reproduit, sans base ni réseau, les
/// verdicts de la maquette en mode dégradé, cycle directionnel entrée/sortie et
/// anti-rejeu local compris. Les QR et la liste sont signés par le vrai service
/// serveur et vérifiés avec la clé publique.
/// </summary>
public class OfflineScanEvaluatorTests
{
    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

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

    // Poste ENTRÉE, aucun visiteur sur site — la configuration par défaut des
    // tests non directionnels.
    private static OfflineVerdict Eval(
        Fixture fx, string qr, OfflineListResult list, DateTimeOffset now,
        string direction = "Entry", IReadOnlySet<Guid>? onSite = null)
        => OfflineScanEvaluator.Evaluate(fx.Verifier, qr, list, direction, onSite ?? None, now);

    [Fact]
    public void ValidQr_InList_WithinWindow_IsRecognized()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        var verdict = Eval(fx, qr, list, now);

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

        var verdict = Eval(fx, new string(chars), list, now);

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

        var verdict = Eval(fx, qr, list, now);

        Assert.Equal(OfflineOutcome.NotInLocalList, verdict.Outcome);
        Assert.False(verdict.IsSecurityEvent);
    }

    [Fact]
    public void ExpiredList_IsUnavailable()
    {
        var fx = new Fixture();
        var issued = DateTimeOffset.UtcNow.AddHours(-5);
        var listNow = fx.Verifier.VerifyDailyList(fx.Server.SignDailyOfflineList(
            Array.Empty<OfflineListEntry>(), issued, issued.AddHours(4)), DateTimeOffset.UtcNow);
        var qr = fx.Server.SignVisitToken(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        var verdict = Eval(fx, qr, listNow, DateTimeOffset.UtcNow);

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

        var verdict = Eval(fx, qr, list, now);

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

        Assert.Equal(OfflineOutcome.TooEarly, Eval(fx, qr, early, now).Outcome);
        Assert.Equal(OfflineOutcome.TooLate, Eval(fx, qr, late, now).Outcome);
    }

    [Fact]
    public void ExpiredQr_IsRejected_EvenInList_OnBusinessDay()
    {
        var fx = new Fixture();
        var friday = new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero); // vendredi (jour ouvré)
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        // Mode 30 jours (ScheduledAt null) : sans contrôle d'expiration hors ligne,
        // ce QR serait « reconnu » un jour ouvré. Son expiration crypto est dépassée.
        var list = fx.SignedList(friday, friday.AddHours(4), new OfflineListEntry(visitId, token, null, false));
        var qr = fx.Server.SignVisitToken(visitId, token, friday.AddMinutes(-1)); // déjà expiré

        var verdict = Eval(fx, qr, list, friday);

        Assert.Equal(OfflineOutcome.Expired, verdict.Outcome);
        Assert.True(verdict.IsSecurityEvent);
        Assert.Equal(token, verdict.VisitToken); // token propagé pour journalisation au resync
    }

    [Fact]
    public void DeniedOfflineScan_CarriesVisitToken_ForResync()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now.AddHours(-1), false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddHours(2));

        var verdict = Eval(fx, qr, list, now);

        Assert.Equal(OfflineOutcome.TooLate, verdict.Outcome);
        Assert.Equal(token, verdict.VisitToken);
    }

    [Fact]
    public void ThirtyDayMode_OnWeekend_IsNonBusinessDay()
    {
        var fx = new Fixture();
        var saturday = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero); // samedi
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(saturday, saturday.AddHours(4), new OfflineListEntry(visitId, token, null, false));
        var qr = fx.Server.SignVisitToken(visitId, token, saturday.AddDays(1));

        var verdict = Eval(fx, qr, list, saturday);

        Assert.Equal(OfflineOutcome.NonBusinessDay, verdict.Outcome);
        Assert.True(verdict.IsSecurityEvent);
    }

    // ---- Cycle directionnel + anti-rejeu local (constat #1) ----

    [Fact]
    public void EntryPost_WhenAlreadyOnSite_IsSuspectedDuplicate_SecurityEvent()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        // Le visiteur est DÉJÀ sur site : re-scanner à l'entrée = suspicion de copie.
        var verdict = Eval(fx, qr, list, now, "Entry", new HashSet<Guid> { token });

        Assert.Equal(OfflineOutcome.SuspectedDuplicate, verdict.Outcome);
        Assert.True(verdict.IsSecurityEvent);
    }

    [Fact]
    public void ExitPost_WhenOnSite_IsCheckedOut_AndNeverBlocked()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        // Hors fenêtre à l'entrée, mais une SORTIE n'est jamais bloquée.
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now.AddHours(-3), false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddHours(2));

        var verdict = Eval(fx, qr, list, now, "Exit", new HashSet<Guid> { token });

        Assert.Equal(OfflineOutcome.CheckedOut, verdict.Outcome);
        Assert.False(verdict.IsSecurityEvent);
    }

    [Fact]
    public void ExitPost_WhenNotOnSite_IsNoActiveEntry()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, false));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        var verdict = Eval(fx, qr, list, now, "Exit", None);

        Assert.Equal(OfflineOutcome.NoActiveEntry, verdict.Outcome);
        Assert.False(verdict.IsSecurityEvent);
    }

    [Fact]
    public void ExcludedButOnSite_CanStillExit()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var (visitId, token) = (Guid.NewGuid(), Guid.NewGuid());
        // Personne exclue MAIS déjà sur site : elle doit pouvoir sortir (miroir en ligne).
        var list = fx.SignedList(now, now.AddHours(4), new OfflineListEntry(visitId, token, now, true));
        var qr = fx.Server.SignVisitToken(visitId, token, now.AddMinutes(15));

        var verdict = Eval(fx, qr, list, now, "Exit", new HashSet<Guid> { token });

        Assert.Equal(OfflineOutcome.CheckedOut, verdict.Outcome);
    }
}
