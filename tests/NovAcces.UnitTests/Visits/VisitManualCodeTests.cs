using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;
using NovAcces.Domain.Exceptions;
using Xunit;

namespace NovAcces.UnitTests.Visits;

/// <summary>
/// Attribution du code de secours (alternative au QR) sur une visite —
/// voir Visit.AssignManualCode. Visit.cs est une zone sensible (CLAUDE.md
/// §7) : toute modification doit rester couverte par un test.
/// </summary>
public class VisitManualCodeTests
{
    private static Visit NewVisit(DateTimeOffset now) => Visit.Create(
        "Jean Visiteur", "ACME SARL", "Livraison", "host-1",
        AccessMode.Unique, now.AddHours(1), 60, null, null, isExcluded: false, now);

    [Fact]
    public void AssignManualCode_ValidHash_IsStored()
    {
        var visit = NewVisit(DateTimeOffset.UtcNow);

        visit.AssignManualCode("abc123hash");

        Assert.Equal("abc123hash", visit.ManualCodeHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AssignManualCode_EmptyOrWhitespaceHash_Throws(string? hash)
    {
        var visit = NewVisit(DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => visit.AssignManualCode(hash!));
    }

    [Fact]
    public void AssignManualCode_CalledAgain_ReplacesThePreviousHash()
    {
        // Réémission (renvoi d'invitation sur changement d'email) : l'ancien
        // code devient silencieusement invalide, comme une clé API régénérée.
        var visit = NewVisit(DateTimeOffset.UtcNow);
        visit.AssignManualCode("premier-hash");

        visit.AssignManualCode("second-hash");

        Assert.Equal("second-hash", visit.ManualCodeHash);
        Assert.NotEqual("premier-hash", visit.ManualCodeHash);
    }
}
