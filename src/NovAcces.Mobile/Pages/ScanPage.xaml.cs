using NovAcces.Mobile.Services;
using NovAcces.Mobile.ViewModels;
using ZXing.Net.Maui;

namespace NovAcces.Mobile.Pages;

public partial class ScanPage : ContentPage
{
    private readonly ScanViewModel _vm;
    private readonly AgentSession _session;
    private bool _processing;

    public ScanPage(ScanViewModel vm, AgentSession session)
    {
        InitializeComponent();
        _vm = vm;
        _session = session;

        Camera.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.TwoDimensional, // QR
            AutoRotate = true,
            Multiple = false,
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Permission caméra (runtime) : sans elle, aucun scan possible.
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();
        Camera.IsDetecting = status == PermissionStatus.Granted;

        UpdateConnectivityLabel();

        // Précharge la liste hors-ligne signée pour préparer une éventuelle coupure.
        try { await _session.RefreshOfflineListAsync(); } catch { /* best-effort */ }
    }

    private async void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_processing) return;
        var value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(value)) return;

        _processing = true;
        try
        {
            var verdict = await _vm.EvaluateAsync(value);
            await MainThread.InvokeOnMainThreadAsync(() => ShowVerdict(verdict));
            // Retour sonore + vibration : l'agent ne doit pas dépendre du visuel seul.
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(verdict.IsSecurityEvent ? 600 : 200)); } catch { }
        }
        catch
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                ShowVerdict(new ScanVerdict("ERREUR", "Réessayer", "#E67E22", false)));
        }
    }

    private void ShowVerdict(ScanVerdict verdict)
    {
        VerdictTitle.Text = verdict.Title;
        VerdictSubtitle.Text = verdict.Subtitle;
        VerdictOverlay.BackgroundColor = Color.FromArgb(verdict.ColorHex);
        VerdictOverlay.IsVisible = true;
    }

    private void OnDismissVerdict(object? sender, EventArgs e)
    {
        VerdictOverlay.IsVisible = false;
        _processing = false;
    }

    private void OnToggleDirection(object? sender, EventArgs e)
    {
        _session.Direction = _session.Direction == "Entry" ? "Exit" : "Entry";
        DirectionButton.Text = _session.Direction == "Entry" ? "Poste : ENTRÉE" : "Poste : SORTIE";
    }

    private void UpdateConnectivityLabel()
    {
        ConnectivityLabel.Text = _vm.IsOnline ? "● en ligne" : "● hors ligne (mode dégradé)";
        ConnectivityLabel.TextColor = _vm.IsOnline ? Colors.LightGreen : Colors.Orange;
    }
}
