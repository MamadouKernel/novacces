using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Auth;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Auth;

/// <summary>
/// Authentifie un terminal agent par clé API (en-tête X-Api-Key), contre
/// l'annuaire des terminaux enrôlés en base (<see cref="ITerminalDirectory"/>).
/// En cas de succès :
///  - terminal à un seul site (cas courant) : le principal reçoit directement
///    le claim SiteId de ce site — c'est lui (et non un en-tête X-Site-Id)
///    qui détermine le tenant, comme avant.
///  - terminal partagé entre plusieurs sites : le principal reçoit un claim
///    AllowedSite par site autorisé (pas de SiteId unique, ambigu). Le tenant
///    est alors résolu par TenantResolutionMiddleware à partir de l'en-tête
///    X-Site-Id, choisi par l'agent à la prise de poste, mais REVALIDÉ contre
///    cette liste de claims — jamais un site arbitraire.
///
/// La recherche du terminal compare une EMPREINTE (SHA-256) de la clé, jamais
/// la clé en clair — voir NovAcces.Infrastructure.Identity.TerminalDirectory
/// pour le choix de hachage rapide (recherche directe) plutôt que PBKDF2.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ITerminalDirectory _terminals;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITerminalDirectory terminals)
        : base(options, logger, encoder)
    {
        _terminals = terminals;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Pas d'en-tête = ce n'est pas une tentative par clé API : on laisse la
        // main aux autres schémas (NoResult, pas Fail).
        if (!Request.Headers.TryGetValue(ApiKeyOptions.HeaderName, out var provided)
            || string.IsNullOrWhiteSpace(provided))
            return AuthenticateResult.NoResult();

        var terminal = await _terminals.VerifyAsync(provided.ToString(), Context.RequestAborted);
        if (terminal is null)
            return AuthenticateResult.Fail("Clé API inconnue ou révoquée.");

        if (terminal.SiteIds.Count == 0)
            return AuthenticateResult.Fail("Terminal mal configuré : aucun site autorisé.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(terminal.Label) ? "terminal" : terminal.Label),
            new(ClaimTypes.Role, NovAccesRoles.Agent),
            new(NovAccesClaimTypes.TerminalId, terminal.Id.ToString("D")),
        };

        if (terminal.SiteIds.Count == 1)
        {
            claims.Add(new Claim(NovAccesClaimTypes.SiteId, terminal.SiteIds[0]));
        }
        else
        {
            foreach (var siteId in terminal.SiteIds)
                claims.Add(new Claim(NovAccesClaimTypes.AllowedSite, siteId));
        }

        var identity = new ClaimsIdentity(claims, ApiKeyOptions.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyOptions.Scheme);
        return AuthenticateResult.Success(ticket);
    }
}
