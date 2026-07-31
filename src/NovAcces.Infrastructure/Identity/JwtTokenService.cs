using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovAcces.Application.Abstractions;
using NovAcces.Shared.Auth;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Émet des JWT signés en HMAC-SHA256. Les claims portent l'identité, les rôles
/// et le site de rattachement (NovAccesClaimTypes.SiteId) — ce dernier étant la
/// source de vérité du tenant pour une requête authentifiée.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.SigningKey) || _options.SigningKey.Length < 32)
            throw new InvalidOperationException(
                "Jwt:SigningKey manquant ou trop court (>= 32 caractères requis pour HMAC-SHA256). " +
                "À définir via user-secrets (dev) ou variable d'environnement (prod).");
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateToken(
        Guid userId, string email, string displayName, IEnumerable<string> roles, string? siteId)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, displayName),
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        if (!string.IsNullOrWhiteSpace(siteId))
            claims.Add(new Claim(NovAccesClaimTypes.SiteId, siteId));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    // Claims du jeton de poste — noms CUSTOM (préfixe nva_) pour qu'ils ne
    // subissent PAS le remappage in/out de JwtSecurityTokenHandler (qui
    // transforme p.ex. "sub" en NameIdentifier et casserait la relecture).
    private const string ClaimMatricule = "nva_mat";
    private const string ClaimAgentName = "nva_name";
    private const string ClaimShiftMarker = "nva_shift"; // "1" = jeton de poste
    private const string ClaimTerminalId = "nva_terminal";

    public (string Token, DateTimeOffset ExpiresAt) CreateShiftToken(string matricule, string displayName, string siteId, Guid terminalId)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(Math.Max(1, _options.ShiftExpiryHours));

        var claims = new List<Claim>
        {
            new(ClaimMatricule, matricule),
            new(ClaimTypes.Name, matricule),
            new(ClaimTypes.Role, NovAccesRoles.Agent),
            new(ClaimAgentName, displayName),
            new(NovAccesClaimTypes.SiteId, siteId),
            new(ClaimTerminalId, terminalId.ToString("D")),
            new(ClaimShiftMarker, "1"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer, audience: _options.Audience, claims: claims,
            notBefore: now.UtcDateTime, expires: expiresAt.UtcDateTime, signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateAgentToken(string matricule, string displayName, string siteId)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(Math.Max(1, _options.AgentExpiryHours));
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, matricule),
            new(ClaimTypes.Role, NovAccesRoles.Agent),
            new(ClaimMatricule, matricule),
            new(ClaimAgentName, displayName),
            new(NovAccesClaimTypes.SiteId, siteId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer, audience: _options.Audience, claims: claims,
            notBefore: now.UtcDateTime, expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);
        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public ShiftIdentity? ValidateShiftToken(string token, string expectedSiteId, Guid? expectedTerminalId = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = _options.Issuer,
            ValidateAudience = true, ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = key,
            ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
        };

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, parameters, out _);

            // Doit être un jeton de POSTE, et rattaché au site attendu (celui du
            // terminal) — un jeton d'un autre site ne vaut rien ici.
            if (principal.FindFirst(ClaimShiftMarker)?.Value != "1") return null;
            var siteId = principal.FindFirst(NovAccesClaimTypes.SiteId)?.Value;
            if (!string.Equals(siteId, expectedSiteId, StringComparison.Ordinal)) return null;

            var terminalClaim = principal.FindFirst(ClaimTerminalId)?.Value;
            if (expectedTerminalId.HasValue
                && (!Guid.TryParse(terminalClaim, out var tokenTerminalId)
                    || tokenTerminalId != expectedTerminalId.Value))
                return null;

            var matricule = principal.FindFirst(ClaimMatricule)?.Value;
            var name = principal.FindFirst(ClaimAgentName)?.Value ?? matricule;
            var tokenTerminalIdOptional = Guid.TryParse(terminalClaim, out var parsedTerminalId)
                ? parsedTerminalId : (Guid?)null;
            return string.IsNullOrWhiteSpace(matricule)
                ? null
                : new ShiftIdentity(matricule, name!, siteId!, tokenTerminalIdOptional);
        }
        catch
        {
            return null; // signature invalide, expiré, ou malformé
        }
    }
}
