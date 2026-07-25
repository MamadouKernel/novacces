using NovAcces.Web.Services;
using Xunit;

namespace NovAcces.WebTests;

/// <summary>
/// Génération du slug d'identifiant de site (nom libre → identifiant technique
/// valide pour le schéma PostgreSQL). Logique auparavant seulement raisonnée.
/// </summary>
public sealed class SiteSlugTests
{
    [Theory]
    [InlineData("Côte d'Ivoire Terminal", "cote_d_ivoire_terminal")]
    [InlineData("SIPRA", "sipra")]
    [InlineData("  Bolloré  Transport ", "bollore_transport")]
    [InlineData("déjà_valide", "deja_valide")]
    [InlineData("sicopa", "sicopa")]
    [InlineData("", "")]
    [InlineData("!!!", "")]
    public void From_ProduitUnSlugValide(string input, string expected)
        => Assert.Equal(expected, SiteSlug.From(input));

    [Fact]
    public void From_RespecteLaLongueurMaximale()
    {
        var slug = SiteSlug.From(new string('a', 100));
        Assert.True(slug.Length <= SiteSlug.MaxLength);
    }

    [Fact]
    public void From_NeContientQueDesCaracteresAutorises()
    {
        var slug = SiteSlug.From("Écran @ Test #1 — Zone/Nord");
        Assert.Matches("^[a-z0-9_]*$", slug);
    }
}
