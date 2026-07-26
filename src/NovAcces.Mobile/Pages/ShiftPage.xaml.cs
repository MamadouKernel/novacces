using Microsoft.Extensions.DependencyInjection;
using NovAcces.Mobile.Services;

namespace NovAcces.Mobile.Pages;

/// <summary>
/// Prise de poste : l'agent s'identifie (matricule + PIN), vérifié côté serveur.
/// En cas de succès, on bascule sur le poste de contrôle ; chaque scan sera alors
/// tracé au matricule de l'agent (traçabilité individuelle, §8.5).
/// </summary>
public partial class ShiftPage : ContentPage
{
    private readonly AgentSession _session;
    private readonly IServiceProvider _services;

    public ShiftPage(AgentSession session, IServiceProvider services)
    {
        InitializeComponent();
        _session = session;
        _services = services;
    }

    private async void OnStartShift(object? sender, EventArgs e)
    {
        var matricule = MatriculeEntry.Text?.Trim() ?? "";
        var pin = PinEntry.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(matricule) || string.IsNullOrWhiteSpace(pin))
        {
            StatusLabel.Text = "Matricule et PIN requis.";
            return;
        }

        StartButton.IsEnabled = false;
        StatusLabel.Text = "Vérification…";
        try
        {
            var ok = await _session.StartShiftAsync(matricule, pin);
            if (!ok)
            {
                StatusLabel.Text = "Matricule ou PIN incorrect.";
                StartButton.IsEnabled = true;
                return;
            }

            // Poste ouvert → écran de scan.
            var scan = _services.GetRequiredService<ScanPage>();
            if (Application.Current?.Windows.Count > 0)
                Application.Current.Windows[0].Page = new NavigationPage(scan);
        }
        catch
        {
            StatusLabel.Text = "Service indisponible — réessayez.";
            StartButton.IsEnabled = true;
        }
    }
}
