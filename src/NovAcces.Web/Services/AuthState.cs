namespace NovAcces.Web.Services;

/// <summary>
/// État d'authentification du portail, porté par le circuit Blazor Server
/// (scoped par connexion). Conserve le JWT obtenu de l'API et l'identité de
/// l'utilisateur connecté. À un rechargement complet de page, un nouveau circuit
/// est créé et l'état est réinitialisé (l'utilisateur se reconnecte) — suffisant
/// pour ce premier incrément ; une persistance de session viendra ensuite.
/// </summary>
public sealed class AuthState
{
    public string? AccessToken { get; private set; }
    public string? DisplayName { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();
    public string? SiteId { get; private set; }

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
}
