using System.Security.Claims;
using NovAcces.Application.Abstractions;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Attribution d'un scan à un agent, partagée entre POST /api/scan(/manual-code)
/// et POST /api/agent/resync. Si un jeton de poste valide ET toujours actif
/// (poste non clos par POST /api/agent/shift/end, ni remplacé par une prise de
/// poste plus récente sur le même terminal) est présent, on trace au matricule
/// (traçabilité individuelle, §8.5) ; sinon repli sur l'identité du terminal.
/// Un jeton de poste clos ne doit plus jamais attribuer les scans à l'agent
/// parti, même s'il reste cryptographiquement valide jusqu'à son expiration
/// naturelle (le JWT lui-même est stateless — c'est ITerminalDirectory qui
/// porte la notion de « poste actif »).
/// </summary>
internal static class AgentAttribution
{
    public static async Task<string> ResolveAgentIdAsync(
        ClaimsPrincipal user, HttpRequest http, IJwtTokenService jwt,
        ITerminalDirectory terminals, ICurrentTenant tenant, CancellationToken ct)
    {
        // "nva_mat" : claim matricule porté par un jeton Agent (login direct,
        // sans prise de poste) — voir JwtTokenService.CreateAgentToken.
        var fallback = user.FindFirstValue("nva_mat")
            ?? user.FindFirstValue(ClaimTypes.Name)
            ?? "terminal-inconnu";

        var shiftToken = http.Headers["X-Shift-Token"].ToString();
        if (string.IsNullOrWhiteSpace(shiftToken))
            return fallback;

        var terminalId = Guid.TryParse(user.FindFirstValue(NovAccesClaimTypes.TerminalId), out var parsedTerminalId)
            ? parsedTerminalId : (Guid?)null;
        if (terminalId is null)
            return fallback;

        var shift = jwt.ValidateShiftToken(shiftToken, tenant.SiteId, terminalId);
        if (shift?.Jti is null)
            return fallback;

        var isActive = await terminals.IsShiftActiveAsync(terminalId.Value, shift.Jti, ct);
        return isActive ? shift.Matricule : fallback;
    }
}
