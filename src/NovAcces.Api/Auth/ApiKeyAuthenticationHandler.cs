using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NovAcces.Infrastructure.Auth;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Auth;

/// <summary>
/// Authentifie un terminal agent par clé API (en-tête X-Api-Key), contre la
/// liste des terminaux enrôlés. En cas de succès, le principal reçoit le rôle
/// Agent et le claim SiteId du terminal — c'est ce SiteId (et non un en-tête
/// X-Site-Id) qui détermine le tenant du scan.
///
/// La comparaison des clés est à temps constant (FixedTimeEquals) pour ne pas
/// fuiter d'information par mesure de durée. C'est un concern web (ASP.NET
/// Core Authentication), d'où sa place dans la couche Api et non Infrastructure.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiKeyOptions _apiKeys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<ApiKeyOptions> apiKeys)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Pas d'en-tête = ce n'est pas une tentative par clé API : on laisse la
        // main aux autres schémas (NoResult, pas Fail).
        if (!Request.Headers.TryGetValue(ApiKeyOptions.HeaderName, out var provided)
            || string.IsNullOrWhiteSpace(provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = provided.ToString();
        var terminal = FindMatchingTerminal(presented);

        if (terminal is null)
            return Task.FromResult(AuthenticateResult.Fail("Clé API inconnue."));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(terminal.Label) ? "terminal" : terminal.Label),
            new Claim(ClaimTypes.Role, NovAccesRoles.Agent),
            new Claim(NovAccesClaimTypes.SiteId, terminal.SiteId),
        };

        var identity = new ClaimsIdentity(claims, ApiKeyOptions.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyOptions.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private EnrolledTerminal? FindMatchingTerminal(string presentedKey)
    {
        var presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        EnrolledTerminal? match = null;

        // On parcourt TOUS les terminaux même après avoir trouvé, pour ne pas
        // court-circuiter et introduire une différence de durée exploitable.
        foreach (var terminal in _apiKeys.Terminals)
        {
            var candidateBytes = Encoding.UTF8.GetBytes(terminal.Key ?? string.Empty);
            if (candidateBytes.Length == presentedBytes.Length
                && CryptographicOperations.FixedTimeEquals(candidateBytes, presentedBytes))
            {
                match = terminal;
            }
        }

        return match;
    }
}
