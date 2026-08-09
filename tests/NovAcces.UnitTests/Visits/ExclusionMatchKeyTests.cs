using NovAcces.Domain.Entities;
using Xunit;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Sémantique de correspondance discutée et validée le 09/08/2026 : le nom
/// normalisé reste le filet large par défaut (REQ-F-11) ; l'email, quand
/// renseigné sur l'ENTRÉE, précise l'exclusion pour ne viser qu'une personne
/// spécifique — sans jamais faire disparaître une entrée sans email.
/// </summary>
public class ExclusionMatchKeyTests
{
    [Fact]
    public void EntryWithoutEmail_MatchesAnyVisitorWithSameName_RegardlessOfEmail()
    {
        var entry = new ExclusionMatchKey(ExclusionEntry.Normalize("Konate Mamadou"), NormalizedEmail: null);

        Assert.True(entry.Matches(
            ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("mamadou@a.com")));
        Assert.True(entry.Matches(ExclusionEntry.Normalize("Konate Mamadou"), null)); // sans email non plus
    }

    [Fact]
    public void EntryWithEmail_OnlyMatchesWhenBothNameAndEmailMatch()
    {
        var entry = new ExclusionMatchKey(
            ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("fraudeur@a.com"));

        Assert.True(entry.Matches(
            ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("fraudeur@a.com")));
    }

    [Fact]
    public void EntryWithEmail_DoesNotMatchSameNameDifferentEmail_HomonymPasses()
    {
        // Le cas central de la demande du 09/08/2026 : deux "Konate Mamadou"
        // distincts, un seul réellement visé par l'exclusion.
        var entry = new ExclusionMatchKey(
            ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("fraudeur@a.com"));

        Assert.False(entry.Matches(
            ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("autre.personne@b.com")));
    }

    [Fact]
    public void EntryWithEmail_DoesNotMatchVisitorWithNoEmail()
    {
        // Une entrée précisée par email ne "redevient" jamais large : un
        // visiteur sans email ne correspond pas à CETTE entrée (il pourrait
        // en revanche correspondre à une AUTRE entrée, sans email, si elle existe).
        var entry = new ExclusionMatchKey(
            ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("fraudeur@a.com"));

        Assert.False(entry.Matches(ExclusionEntry.Normalize("Konate Mamadou"), null));
    }

    [Fact]
    public void AnyMatches_FallsBackToBroadEntry_WhenPreciseEntryDoesNotMatch()
    {
        // Une entrée large ET une entrée précise peuvent coexister pour le
        // même nom : si l'email ne correspond pas à l'entrée précise, la
        // sûreté reste protégée par l'entrée large si elle en a ajouté une.
        var keys = new[]
        {
            new ExclusionMatchKey(ExclusionEntry.Normalize("Konate Mamadou"), ExclusionEntry.NormalizeEmail("fraudeur@a.com")),
            new ExclusionMatchKey(ExclusionEntry.Normalize("Autre Personne"), null),
        };

        Assert.True(ExclusionMatchKey.AnyMatches(keys, "Autre Personne", null));
        Assert.False(ExclusionMatchKey.AnyMatches(keys, "Konate Mamadou", "innocent@b.com"));
        Assert.True(ExclusionMatchKey.AnyMatches(keys, "Konate Mamadou", "fraudeur@a.com"));
    }

    [Fact]
    public void NormalizeEmail_IsCaseInsensitive_AndTrimsWhitespace()
    {
        Assert.Equal(ExclusionEntry.NormalizeEmail(" Fraudeur@A.com "), ExclusionEntry.NormalizeEmail("fraudeur@a.com"));
    }

    [Fact]
    public void NormalizeEmail_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(ExclusionEntry.NormalizeEmail(null));
        Assert.Null(ExclusionEntry.NormalizeEmail("   "));
    }
}
