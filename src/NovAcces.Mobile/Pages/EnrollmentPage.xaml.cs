using Microsoft.Extensions.DependencyInjection;
using NovAcces.Mobile.Services;

namespace NovAcces.Mobile.Pages;

/// <summary>
/// Écran d'enrôlement du terminal : saisie de l'URL de l'API, de la clé API et de
/// la clé publique ES256. Enregistre dans le stockage sécurisé (AgentConfig.SaveAsync)
/// puis bascule sur le poste de contrôle. Affiché au premier lancement tant que le
/// terminal n'est pas enrôlé.
/// </summary>
public partial class EnrollmentPage : ContentPage
{
    private readonly AgentConfig _config;
    private readonly IServiceProvider _services;

    public EnrollmentPage(AgentConfig config, IServiceProvider services)
    {
        InitializeComponent();
        _config = config;
        _services = services;

        // Pré-remplissage si des valeurs existent déjà (ré-enrôlement).
        UrlEntry.Text = _config.ApiBaseUrl;
        ApiKeyEntry.Text = _config.ApiKey;
        PublicKeyEditor.Text = _config.PublicKeyPem;
    }

    private async void OnEnroll(object? sender, EventArgs e)
    {
        var url = UrlEntry.Text?.Trim() ?? "";
        var apiKey = ApiKeyEntry.Text?.Trim() ?? "";
        var pem = PublicKeyEditor.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(pem))
        {
            StatusLabel.Text = "URL, clé API et clé publique sont toutes requises.";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            StatusLabel.Text = "URL invalide.";
            return;
        }

        EnrollButton.IsEnabled = false;
        try
        {
            _config.ApiBaseUrl = url;
            _config.ApiKey = apiKey;
            _config.PublicKeyPem = pem;
            await _config.SaveAsync();

            // Bascule sur le poste de contrôle (résolu maintenant que la config est
            // renseignée, pour que l'API client et le vérificateur soient bien câblés).
            var scan = _services.GetRequiredService<ScanPage>();
            if (Application.Current?.Windows.Count > 0)
                Application.Current.Windows[0].Page = new NavigationPage(scan);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Échec de l'enrôlement : {ex.Message}";
            EnrollButton.IsEnabled = true;
        }
    }
}
