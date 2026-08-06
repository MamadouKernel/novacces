using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Maui.Media;
using Microsoft.Maui.Networking;
using NovAcces.Mobile.Services;
using NovAcces.Shared.Dtos;
using NovAcces.Shared.Offline;
using ZXing.Net.Maui;

namespace NovAcces.Mobile.Pages;

/// <summary>
/// Premier lancement : lecture d'un QR d'invitation temporaire. Le mobile génère
/// sa paire de clés, active le terminal auprès de l'API puis conserve uniquement
/// les secrets dans SecureStorage.
/// </summary>
public partial class EnrollmentPage : ContentPage
{
    // Pré-remplissage du mode manuel : seul déploiement en usage réel à ce
    // jour (voir .env.example, API_PUBLIC_BASE_URL) — le champ reste éditable
    // pour couvrir un site pilote sur une autre URL.
    private const string DefaultApiBaseUrl = "https://api.sigasacces.com";

    private readonly AgentConfig _config;
    private readonly IServiceProvider _services;
    private bool _processing;
    private string? _apiBaseUrl;
    private string? _ticket;
    private string? _deviceInstanceId;
    private string? _devicePrivateKeyPem;
    private string? _devicePublicKeyPem;

    public EnrollmentPage(AgentConfig config, IServiceProvider services)
    {
        InitializeComponent();
        _config = config;
        _services = services;

        Camera.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional,
            AutoRotate = true,
            Multiple = false,
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        Camera.IsDetecting = status == PermissionStatus.Granted;
        if (status != PermissionStatus.Granted)
            ScanStatusLabel.Text = "Autorisation caméra requise pour scanner le QR.";
    }

    protected override void OnDisappearing()
    {
        Camera.IsDetecting = false;
        base.OnDisappearing();
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_processing) return;
        var value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value)) return;

        _processing = true;
        Camera.IsDetecting = false;
        await MainThread.InvokeOnMainThreadAsync(() => ReadEnrollmentQr(value));
    }

    private void ReadEnrollmentQr(string value)
    {
        try
        {
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "novacces", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "enroll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ce QR n'est pas un QR d'enrôlement NovAcces.");

            var query = ParseQuery(uri.Query);
            if (!query.TryGetValue("api", out var api)
                || !query.TryGetValue("ticket", out var ticket)
                || !query.TryGetValue("expires", out var expiresText)
                || !Uri.TryCreate(api, UriKind.Absolute, out var apiUri)
                || (apiUri.Scheme is not ("http" or "https"))
                || !long.TryParse(expiresText, out var expiresUnix))
                throw new InvalidOperationException("QR incomplet ou mal formé.");

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
            if (expiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Ce QR d'enrôlement a expiré.");

            var terminalLabelText = query.TryGetValue("terminal", out var terminalId)
                ? $"Identifiant : {terminalId}"
                : "Terminal NovAcces";
            var expirationText = $"Ticket valide jusqu'au {expiresAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}";

            PrepareEnrollment(apiUri.ToString().TrimEnd('/'), ticket, terminalLabelText, expirationText);
            ScanStatusLabel.Text = "QR reconnu. Confirmez l'activation du terminal.";
        }
        catch (Exception ex)
        {
            _processing = false;
            Camera.IsDetecting = true;
            TicketSummary.IsVisible = false;
            EnrollButton.IsEnabled = false;
            ScanStatusLabel.Text = ex.Message;
        }
    }

    private void OnToggleManualMode(object? sender, EventArgs e)
    {
        var showing = !ManualEntrySection.IsVisible;
        ManualEntrySection.IsVisible = showing;
        if (showing && string.IsNullOrWhiteSpace(ApiUrlEntry.Text))
            ApiUrlEntry.Text = DefaultApiBaseUrl;

        // La caméra reste inutile pendant la saisie manuelle — c'est
        // précisément le repli prévu quand elle est hors service.
        Camera.IsDetecting = !showing;
        ToggleManualModeButton.Text = showing
            ? "Revenir au scan du QR"
            : "Pas de caméra ? Saisir le code manuellement";
        ManualStatusLabel.Text = "";
    }

    private void OnManualCodeSubmit(object? sender, EventArgs e)
    {
        var apiText = (ApiUrlEntry.Text ?? "").Trim();
        var code = (ManualCodeEntry.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            ManualStatusLabel.Text = "Saisissez le code fourni par l'administration.";
            return;
        }
        if (!Uri.TryCreate(apiText, UriKind.Absolute, out var apiUri) || apiUri.Scheme is not ("http" or "https"))
        {
            ManualStatusLabel.Text = "URL de l'API invalide.";
            return;
        }

        PrepareEnrollment(apiUri.ToString().TrimEnd('/'), code,
            "Terminal NovAcces (code manuel)",
            "Vérifiez la validité du code auprès de l'administration si l'activation échoue.");
        ManualStatusLabel.Text = "Code reconnu. Confirmez l'activation du terminal.";
    }

    /// <summary>
    /// Étape commune au QR scanné et au code manuel : les deux jouent
    /// exactement le même rôle une fois arrivés ici — même génération de
    /// clés, même écran de confirmation, même appel d'activation dans
    /// <see cref="OnEnroll"/>.
    /// </summary>
    private void PrepareEnrollment(string apiBaseUrl, string ticketValue, string terminalLabelText, string expirationText)
    {
        _apiBaseUrl = apiBaseUrl;
        _ticket = ticketValue;
        _deviceInstanceId = Guid.NewGuid().ToString("D");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _devicePrivateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        _devicePublicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        TerminalLabel.Text = terminalLabelText;
        ExpirationLabel.Text = expirationText;
        TicketSummary.IsVisible = true;
        EnrollButton.IsEnabled = true;
        StatusLabel.Text = "";
    }

    private async void OnEnroll(object? sender, EventArgs e)
    {
        if (_apiBaseUrl is null || _ticket is null || _deviceInstanceId is null
            || _devicePrivateKeyPem is null || _devicePublicKeyPem is null)
            return;

        EnrollButton.IsEnabled = false;
        Camera.IsDetecting = false;
        StatusLabel.Text = "Activation sécurisée en cours…";

        try
        {
            // Preuve de possession : on signe « ticket|deviceInstanceId » avec la
            // clé privée générée sur CET appareil. L'API refuse l'activation si
            // la signature ne correspond pas à la clé publique présentée — un
            // ticket photographié ne suffit donc plus à enrôler un autre
            // appareil à la place du bon.
            var proof = SignProofOfPossession(_devicePrivateKeyPem, _ticket, _deviceInstanceId);

            using var client = new HttpClient { BaseAddress = new Uri(_apiBaseUrl) };
            using var response = await client.PostAsJsonAsync(
                "/api/device-enrollments/activate",
                new DeviceEnrollmentRequestDto(_ticket, _deviceInstanceId, _devicePublicKeyPem, proof));

            if (!response.IsSuccessStatusCode)
            {
                StatusLabel.Text = response.StatusCode == System.Net.HttpStatusCode.Gone
                    ? "Ticket expiré ou déjà utilisé. Demandez un nouveau QR à l'administration."
                    : "Activation refusée par l'API.";
                EnrollButton.IsEnabled = true;
                Camera.IsDetecting = true;
                return;
            }

            var activation = await response.Content.ReadFromJsonAsync<DeviceEnrollmentActivationDto>();
            if (activation is null || string.IsNullOrWhiteSpace(activation.ApiKey))
                throw new InvalidOperationException("Réponse d'activation invalide.");

            var publicKey = await client.GetFromJsonAsync<PublicKeyDto>("/api/keys/public");
            if (publicKey is null || string.IsNullOrWhiteSpace(publicKey.PublicKeyPem))
                throw new InvalidOperationException("Clé publique de vérification indisponible.");

            _config.ApiBaseUrl = _apiBaseUrl;
            _config.ApiKey = activation.ApiKey;
            _config.PublicKeyPem = publicKey.PublicKeyPem;
            _config.PublicKeyId = string.IsNullOrWhiteSpace(publicKey.Kid) ? "current" : publicKey.Kid;
            _config.RetiredPublicKeys = (publicKey.RetiredKeys ?? Array.Empty<PublicKeyEntryDto>())
                .Select(k => new OfflineVerificationKey(k.Kid, k.PublicKeyPem))
                .ToList();
            _config.DeviceInstanceId = _deviceInstanceId;
            _config.DevicePrivateKeyPem = _devicePrivateKeyPem;
            _config.DevicePublicKeyPem = _devicePublicKeyPem;
            await _config.SaveAsync();

            StatusLabel.Text = $"Terminal « {activation.Label} » activé.";
            var shift = _services.GetRequiredService<ShiftPage>();
            if (Application.Current?.Windows.Count > 0)
                Application.Current.Windows[0].Page = new NavigationPage(shift);
        }
        catch
        {
            StatusLabel.Text = "Service indisponible. Demandez un nouveau QR si le ticket a été consommé.";
            EnrollButton.IsEnabled = true;
            Camera.IsDetecting = true;
        }
    }

    /// <summary>
    /// Signe « ticket|deviceInstanceId » en ES256 avec la clé privée du device
    /// et encode en Base64Url. Format miroir de la vérification serveur
    /// (DeviceEnrollmentEndpoints) : toute divergence casserait l'enrôlement.
    /// </summary>
    private static string SignProofOfPossession(string privateKeyPem, string ticket, string deviceInstanceId)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = ecdsa.SignData(
            System.Text.Encoding.UTF8.GetBytes($"{ticket}|{deviceInstanceId}"), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            result[key] = value;
        }
        return result;
    }
}