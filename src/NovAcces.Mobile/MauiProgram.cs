using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using NovAcces.Mobile.Pages;
using NovAcces.Mobile.Services;
using NovAcces.Mobile.ViewModels;
using ZXing.Net.Maui.Controls;

namespace NovAcces.Mobile;

/// <summary>
/// Câblage de l'app agent. À FUSIONNER avec le MauiProgram généré par
/// `dotnet new maui`. Points clés : UseBarcodeReader (ZXing), enregistrement des
/// services agent, et injection de la configuration du terminal (URL, clé API,
/// clé publique) — en production, chargée depuis un stockage sécurisé du terminal.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Configuration du terminal (à externaliser : appsettings embarqué ou
        // stockage sécurisé renseigné à l'enrôlement). Ne JAMAIS embarquer la clé
        // privée : uniquement la clé PUBLIQUE de vérification.
        var config = new AgentConfig
        {
            ApiBaseUrl = "https://sicopa.novacces.ci",
            ApiKey = "<clé API du terminal enrôlé>",
            PublicKeyPem = "<clé publique ES256 (PEM)>",
        };
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IConnectivity>(Connectivity.Current);
        builder.Services.AddSingleton(_ => new HttpClient());
        builder.Services.AddSingleton<AgentApiClient>();
        builder.Services.AddSingleton<AgentSession>();
        builder.Services.AddTransient<ScanViewModel>();
        builder.Services.AddTransient<ScanPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
