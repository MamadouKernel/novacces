using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Web.Components.Controls;
using NovAcces.Web.Services;
using Xunit;

namespace NovAcces.WebTests;

/// <summary>
/// Système de notifications (toasts) — vérifie que le composant affiche bien
/// les toasts poussés par le service et applique la classe de type attendue.
/// </summary>
public sealed class ToastHostTests : TestContext
{
    [Fact]
    public void SansToast_NAfficheRien()
    {
        Services.AddSingleton(new ToastService());

        var cut = RenderComponent<ToastHost>();

        Assert.Empty(cut.FindAll(".toast"));
    }

    [Fact]
    public void ToastSucces_EstAffiche_AvecClasseOk()
    {
        var toasts = new ToastService();
        Services.AddSingleton(toasts);
        var cut = RenderComponent<ToastHost>();

        toasts.Success("Compte créé.");

        cut.WaitForAssertion(() =>
        {
            var toast = cut.Find(".toast");
            Assert.Contains("Compte créé.", toast.TextContent);
            Assert.Contains("ok", toast.GetAttribute("class"));
        });
    }

    [Fact]
    public void ToastErreur_PorteLaClasseErr()
    {
        var toasts = new ToastService();
        Services.AddSingleton(toasts);
        var cut = RenderComponent<ToastHost>();

        toasts.Error("Échec.");

        cut.WaitForAssertion(() =>
            Assert.Contains("err", cut.Find(".toast").GetAttribute("class")));
    }
}
