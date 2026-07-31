using NovAcces.Mobile.Services;
using NovAcces.Shared.Dtos;

namespace NovAcces.Mobile.Pages;

/// <summary>
/// « Attendus aujourd'hui » (§11) : nom + statut + fenêtre horaire UNIQUEMENT
/// (jamais motif, entreprise ni coordonnées — moindre privilège). Permet aussi
/// la resynchronisation des scans réalisés hors ligne et l'affichage des
/// conflits éventuels (ex. QR révoqué pendant la coupure).
/// </summary>
public partial class ExpectedTodayPage : ContentPage
{
    private readonly AgentApiClient _api;
    private readonly AgentSession _session;

    public ExpectedTodayPage(AgentApiClient api, AgentSession session)
    {
        InitializeComponent();
        _api = api;
        _session = session;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var expected = await _api.GetExpectedTodayAsync();
            List.ItemsSource = expected.Select(ExpectedRow.From).ToList();
        }
        catch
        {
            List.ItemsSource = null; // hors ligne / erreur -> EmptyView
        }

        var pending = await _session.PendingCountAsync();
        Header.Text = pending > 0
            ? $"{pending} scan(s) hors-ligne en attente de resynchronisation."
            : "Aucun scan hors-ligne en attente.";
    }

    private async void OnResync(object? sender, EventArgs e)
    {
        ResyncButton.IsEnabled = false;
        try
        {
            var result = await _session.ResyncAsync();
            if (result is null)
            {
                await DisplayAlert("Resynchronisation", "Échec — réessayez une fois en ligne.", "OK");
            }
            else if (result.Conflicts.Count == 0)
            {
                await DisplayAlert("Resynchronisation",
                    $"{result.Processed} scan(s) confronté(s), aucun conflit.", "OK");
            }
            else
            {
                var lines = string.Join("\n", result.Conflicts.Select(c => $"• {c.VisitorName} — {c.Reason}"));
                await DisplayAlert("⚠ Conflits détectés",
                    $"{result.Processed} scan(s) confronté(s), {result.Conflicts.Count} conflit(s) :\n{lines}", "OK");
            }
        }
        catch
        {
            await DisplayAlert("Resynchronisation", "Impossible de joindre le serveur.", "OK");
        }
        finally
        {
            ResyncButton.IsEnabled = true;
            await LoadAsync();
        }
    }
}

/// <summary>Projection d'affichage d'un attendu (avec fenêtre formatée + couleur de statut).</summary>
public sealed class ExpectedRow
{
    public string VisitorName { get; init; } = "";
    public string Status { get; init; } = "";
    public string Window { get; init; } = "";
    public Color StatusColor { get; init; } = Colors.White;

    public static ExpectedRow From(ExpectedVisitorDto d) => new()
    {
        VisitorName = d.VisitorName,
        Status = d.Status,
        Window = FormatWindow(d.WindowStart, d.WindowEnd),
        StatusColor = d.Status switch
        {
            "sur site" => Color.FromArgb("#26C266"), // vert maquette
            "sorti"    => Color.FromArgb("#1B5E9E"), // bleu maquette
            "révoqué"  => Color.FromArgb("#C92A2A"), // rouge maquette
            _          => Color.FromArgb("#F5A300"), // ambre — attendu
        },
    };

    private static string FormatWindow(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null && end is null) return "—";
        var s = start?.ToLocalTime().ToString("HH:mm") ?? "…";
        var e = end?.ToLocalTime().ToString("HH:mm") ?? "…";
        return $"Fenêtre {s} – {e}";
    }
}
