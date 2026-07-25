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

        // Config du terminal enregistrée VIDE au démarrage — elle sera chargée
        // depuis le stockage sécurisé EN ASYNCHRONE par App (jamais en bloquant
        // le thread principal : un GetAwaiter().GetResult() sur SecureStorage fige
        // l'UI au démarrage sur Android = « application ne répond pas »).
        builder.Services.AddSingleton(new AgentConfig());
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
        builder.Services.AddTransient<EnrollmentPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
