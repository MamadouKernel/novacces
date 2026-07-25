using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
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
        // Initialise le fournisseur SQLite natif (persistance des scans hors-ligne).
        // Idempotent — le bundle s'auto-initialise déjà, ceci est une ceinture.
        SQLitePCL.Batteries_V2.Init();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Configuration du terminal chargée depuis le STOCKAGE SÉCURISÉ (renseigné
        // à l'enrôlement) — plus aucun secret en dur. En cas d'absence (terminal
        // pas encore enrôlé) ou de stockage indisponible, on démarre avec une
        // config vide : l'app fonctionne mais n'est pas enrôlée (AgentConfig.IsEnrolled
        // == false), un écran d'enrôlement appellera AgentConfig.SaveAsync().
        AgentConfig config;
        try { config = AgentConfig.LoadAsync().GetAwaiter().GetResult(); }
        catch { config = new AgentConfig(); }
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<IConnectivity>(Connectivity.Current);
        builder.Services.AddSingleton(_ => new HttpClient());
        builder.Services.AddSingleton<AgentApiClient>();

        // Persistance SQLite des scans hors-ligne (survit à un redémarrage).
        var offlineDbPath = Path.Combine(FileSystem.AppDataDirectory, "novacces-offline.db3");
        builder.Services.AddSingleton(new OfflineScanStore(offlineDbPath));

        builder.Services.AddSingleton<AgentSession>();
        builder.Services.AddTransient<ScanViewModel>();
        builder.Services.AddTransient<ScanPage>();
        builder.Services.AddTransient<ExpectedTodayPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
