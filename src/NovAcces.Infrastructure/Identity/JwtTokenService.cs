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
}
