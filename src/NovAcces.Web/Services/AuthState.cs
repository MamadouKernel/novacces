namespace NovAcces.Web.Services;

/// <summary>
/// État d'authentification du portail, porté par le circuit Blazor Server.
/// Persisté dans le stockage de session chiffré du navigateur (voir
/// AuthPersistence) afin de survivre à un rechargement complet de page.
/// </summary>
public sealed class AuthState
{
    public string? AccessToken { get; private set; }
    public string? DisplayName { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();
    public string? SiteId { get; private set; }

    /// <summary>
    /// Vrai une fois que la restauration depuis le stockage a été tentée. Les
    /// pages attendent ce drapeau avant de décider d'afficher ou de rediriger,
    /// pour ne pas rejeter un utilisateur dont la session n'est pas encore relue.
    /// </summary>
    public bool Initialized { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);
    public bool IsInRole(string role) => Roles.Contains(role);

    public event Action? Changed;

    public void SignIn(string accessToken, string displayName, IReadOnlyList<string> roles, string? siteId)
    {
        AccessToken = accessToken;
        DisplayName = displayName;
        Roles = roles;
        SiteId = siteId;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        AccessToken = null;
        DisplayName = null;
        Roles = Array.Empty<string>();
        SiteId = null;
        Changed?.Invoke();
    }

    /// <summary>Marque la restauration comme effectuée (notifie les abonnés).</summary>
    public void MarkInitialized()
    {
        Initialized = true;
        Changed?.Invoke();
    }

    /// <summary>Instantané persistable (null si non connecté).</summary>
    public PersistedSession? Snapshot() =>
        IsAuthenticated ? new PersistedSession(AccessToken!, DisplayName ?? "", Roles, SiteId) : null;

    /// <summary>Restaure sans notifier (l'événement est levé ensuite par MarkInitialized).</summary>
    public void RestoreSilently(PersistedSession s)
    {
        AccessToken = s.AccessToken;
        DisplayName = s.DisplayName;
        Roles = s.Roles;
        SiteId = s.SiteId;
    }
}

public sealed record PersistedSession(
    string AccessToken, string DisplayName, IReadOnlyList<string> Roles, string? SiteId);
