namespace NovAcces.Web.Services;

public enum ToastKind { Success, Error, Info, Security }

public sealed record Toast(Guid Id, string? Title, string Message, ToastKind Kind);

/// <summary>
/// Notifications visuelles éphémères (toasts) — retour clair après chaque
/// action (« Compte créé », « QR généré », « Erreur »…). Portée circuit
/// (scoped) : une file par utilisateur connecté.
/// </summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = new();

    public IReadOnlyList<Toast> Toasts => _toasts;
    public event Action? Changed;

    public void Success(string message, string? title = "Succès") => Add(title, message, ToastKind.Success);
    public void Error(string message, string? title = "Erreur") => Add(title, message, ToastKind.Error);
    public void Info(string message, string? title = null) => Add(title, message, ToastKind.Info);
    public void Security(string message, string? title = "Sécurité") => Add(title, message, ToastKind.Security);

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
            Changed?.Invoke();
    }

    private void Add(string? title, string message, ToastKind kind)
    {
        var toast = new Toast(Guid.NewGuid(), title, message, kind);
        _toasts.Add(toast);
        Changed?.Invoke();
        _ = AutoDismissAsync(toast);
    }

    private async Task AutoDismissAsync(Toast toast)
    {
        // Les messages d'erreur/sécurité restent affichés plus longtemps.
        var delay = toast.Kind is ToastKind.Error or ToastKind.Security ? 7000 : 4000;
        await Task.Delay(delay);
        Dismiss(toast.Id);
    }
}
