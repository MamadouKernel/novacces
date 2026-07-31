using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Media;
using Microsoft.Maui.Networking;
using NovAcces.Mobile.Services;
using NovAcces.Mobile.ViewModels;
using ZXing.Net.Maui;

namespace NovAcces.Mobile.Pages;

public partial class ScanPage : ContentPage
{
    private readonly ScanViewModel _vm;
    private readonly AgentSession _session;
    private readonly IConnectivity _connectivity;
    private readonly IServiceProvider _services;
    private bool _processing;
    private bool _resyncing;
    private bool _soundOn = true;
    private int _scanCount;

    public ScanPage(ScanViewModel vm, AgentSession session, IConnectivity connectivity, IServiceProvider services)
    {
        InitializeComponent();
        _vm = vm;
        _session = session;
        _connectivity = connectivity;
        _services = services;

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

        // Bandeau connectivité dynamique (#5) : suivre les changements réseau en
        // continu, pas seulement à l'ouverture.
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        UpdateConnectivityLabel();

        // Agent en poste (matricule) affiché en pied — traçabilité visible.
        AgentLabel.Text = _session.IsShiftActive
            ? $"Agent : {_session.AgentMatricule}"
            : "Poste de contrôle";

        // Précharge la liste hors-ligne signée pour préparer une éventuelle coupure.
        try { await _session.RefreshOfflineListAsync(); } catch { /* best-effort */ }
    }

    protected override void OnDisappearing()
    {
        _connectivity.ConnectivityChanged -= OnConnectivityChanged;
        base.OnDisappearing();
    }

    // #4/#5 : à la reconnexion, rafraîchir la liste hors-ligne et resynchroniser
    // automatiquement les scans en attente (les conflits sont des événements de
    // sécurité et sont signalés à l'agent).
    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        await MainThread.InvokeOnMainThreadAsync(UpdateConnectivityLabel);
        if (_vm.IsOnline)
            await RefreshAndResyncAsync();
    }

    private async Task RefreshAndResyncAsync()
    {
        if (_resyncing) return;
        _resyncing = true;
        try
        {
            try { await _session.RefreshOfflineListAsync(); } catch { /* best-effort */ }

            if (await _session.PendingCountAsync() == 0) return;

            var result = await _session.ResyncAsync();
            if (result is { Conflicts.Count: > 0 })
            {
                var lines = string.Join("\n", result.Conflicts.Select(c => $"• {c.VisitorName} — {c.Reason}"));
                await MainThread.InvokeOnMainThreadAsync(() => DisplayAlert(
                    "⚠ Conflits à la resynchronisation",
                    $"{result.Conflicts.Count} conflit(s) détecté(s) :\n{lines}", "OK"));
            }
        }
        catch { /* on retentera au prochain changement de connectivité */ }
        finally { _resyncing = false; }
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
            _scanCount++;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ShowVerdict(verdict);
                CountLabel.Text = _scanCount == 1 ? "1 scan aujourd'hui" : $"{_scanCount} scans aujourd'hui";
            });

            // Retour non-visuel : l'agent ne doit pas dépendre du visuel seul.
            // Vibration + énoncé vocal, sauf si le son est coupé (bouton 🔇).
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(verdict.IsSecurityEvent ? 600 : 200)); } catch { }
            if (_soundOn)
                try { _ = TextToSpeech.Default.SpeakAsync(verdict.Title); } catch { }
        }
        catch
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                ShowVerdict(new ScanVerdict("Erreur", "Réessayer", "#E0932A", false)));
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
        DirectionButton.Text = _session.Direction == "Entry" ? "Poste : entrée" : "Poste : sortie";
    }

    // Coupe/rétablit l'énoncé vocal du verdict (la vibration reste toujours active).
    private void OnToggleSound(object? sender, EventArgs e)
    {
        _soundOn = !_soundOn;
        SoundButton.Text = _soundOn ? "🔊" : "🔇";
    }

    // Ouvre l'écran « attendus du jour » + resynchronisation (résolu par DI).
    private async void OnOpenExpected(object? sender, EventArgs e)
        => await Navigation.PushAsync(_services.GetRequiredService<Pages.ExpectedTodayPage>());

    // Fin de poste : l'agent quitte, retour à la prise de poste (le suivant devra
    // s'identifier). Les scans ne seront plus tracés à ce matricule.
    private async void OnEndShift(object? sender, EventArgs e)
    {
        if (!await DisplayAlert("Fin de poste", "Quitter votre poste ?", "Oui", "Non"))
            return;
        _session.EndShift();
        var shift = _services.GetRequiredService<Pages.ShiftPage>();
        if (Application.Current?.Windows.Count > 0)
            Application.Current.Windows[0].Page = new NavigationPage(shift);
    }

    private void UpdateConnectivityLabel()
    {
        ConnectivityLabel.Text = _vm.IsOnline ? "● en ligne" : "● hors ligne (mode dégradé)";
        ConnectivityLabel.TextColor = _vm.IsOnline ? Colors.LightGreen : Colors.Orange;
    }
}
