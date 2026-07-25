using Bunit;
using NovAcces.Web.Components.Controls;
using Xunit;

namespace NovAcces.WebTests;

/// <summary>
/// Composant PasswordBox (« l'œil » afficher/masquer) — réduit l'angle mort UI
/// signalé dans l'audit : ce comportement était jusqu'ici non testé.
/// </summary>
public sealed class PasswordBoxTests : TestContext
{
    [Fact]
    public void MotDePasse_MasquePorDefaut()
    {
        var cut = RenderComponent<PasswordBox>(p => p.Add(x => x.Value, "secret"));

        Assert.Equal("password", cut.Find("input").GetAttribute("type"));
    }

    [Fact]
    public void ClicSurOeil_BasculeAffichageDuMotDePasse()
    {
        var cut = RenderComponent<PasswordBox>(p => p.Add(x => x.Value, "secret"));

        cut.Find("button.pwd-eye").Click();          // afficher
        Assert.Equal("text", cut.Find("input").GetAttribute("type"));

        cut.Find("button.pwd-eye").Click();          // masquer de nouveau
        Assert.Equal("password", cut.Find("input").GetAttribute("type"));
    }

    [Fact]
    public void Saisie_RemonteLaValeur()
    {
        string? captured = null;
        var cut = RenderComponent<PasswordBox>(p => p
            .Add(x => x.Value, "")
            .Add(x => x.ValueChanged, v => captured = v));

        cut.Find("input").Input("nouveau-mdp");

        Assert.Equal("nouveau-mdp", captured);
    }
}
