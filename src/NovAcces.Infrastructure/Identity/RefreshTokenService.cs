using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Identity;

/// <summary>Refresh tokens aléatoires, à usage unique et révocables.</summary>
public sealed class RefreshTokenService : IRefreshTokenService
{
    private const string TokenPurpose = "NovAcces.RefreshToken.v1";
    private readonly NovAccesIdentityDbContext _db;
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        NovAccesIdentityDbContext db,
        IOptions<JwtOptions> jwtOptions,
        IDateTimeProvider clock,
        ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _jwtOptions = jwtOptions;
        _clock = clock;
        _logger = logger;
    }

    public async Task<RefreshTokenIssued> IssueAsync(string subjectType, string subjectId, string? displayName, string? siteId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var expiresAt = now.AddHours(Math.Max(1, _jwtOptions.Value.RefreshExpiryHours));
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        _db.RefreshSessions.Add(RefreshSession.Create(subjectType, subjectId, displayName, siteId, Hash(raw), now, expiresAt));
        await _db.SaveChangesAsync(ct);
        return new RefreshTokenIssued(Protect(raw), expiresAt);
    }

    public async Task<RefreshTokenSubject?> RotateAsync(string refreshToken, CancellationToken ct)
    {
        if (!TryUnprotect(refreshToken, out var raw))
            return null;

        var now = _clock.UtcNow;
        var hash = Hash(raw);
        var session = await _db.RefreshSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (session is null)
            return null;

        // DÉTECTION DE RÉUTILISATION. Les jetons sont à usage unique : un jeton
        // DÉJÀ consommé qu'on nous représente signifie qu'il a fuité (vol de
        // stockage, rejeu réseau) — soit le légitime rejoue, soit l'attaquant
        // utilise sa copie, et on ne peut pas distinguer les deux. Le refuser
        // seul ne suffit pas : celui des deux qui détient le jeton SUIVANT
        // garde un accès valide. On révoque donc toute la lignée du sujet, ce
        // qui force une réauthentification complète — la seule issue sûre.
        //
        // Un jeton simplement EXPIRÉ n'est pas suspect : c'est le cours normal
        // des choses, on refuse sans rien révoquer.
        if (session.RevokedAt is not null)
        {
            await RevokeAllForSubjectAsync(session.SubjectType, session.SubjectId, ct);
            _logger.LogWarning(
                "Réutilisation d'un refresh token déjà consommé ({SubjectType}/{SubjectId}) : "
                + "toutes les sessions de ce sujet ont été révoquées.",
                session.SubjectType, session.SubjectId);
            return null;
        }

        if (session.ExpiresAt <= now)
            return null;

        // Rotation atomique : une seule requête concurrente peut satisfaire
        // la clause RevokedAt IS NULL.
        var affected = await _db.RefreshSessions
            .Where(x => x.Id == session.Id && x.RevokedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), ct);

        // Perdu la course : une autre requête vient de consommer CE jeton. Deux
        // usages simultanés du même jeton à usage unique = même signal que
        // ci-dessus, même réponse.
        if (affected != 1)
        {
            await RevokeAllForSubjectAsync(session.SubjectType, session.SubjectId, ct);
            _logger.LogWarning(
                "Usage concurrent d'un refresh token ({SubjectType}/{SubjectId}) : "
                + "toutes les sessions de ce sujet ont été révoquées.",
                session.SubjectType, session.SubjectId);
            return null;
        }

        return new RefreshTokenSubject(session.SubjectType, session.SubjectId, session.DisplayName, session.SiteId);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        if (!TryUnprotect(refreshToken, out var raw)) return;
        var session = await _db.RefreshSessions.SingleOrDefaultAsync(x => x.TokenHash == Hash(raw), ct);
        if (session is null || session.RevokedAt is not null) return;
        session.Revoke(_clock.UtcNow);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllForSubjectAsync(string subjectType, string subjectId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var sessions = await _db.RefreshSessions
            .Where(x => x.SubjectType == subjectType && x.SubjectId == subjectId && x.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions)
            session.Revoke(now);
        if (sessions.Count > 0)
            await _db.SaveChangesAsync(ct);
    }
    private static string Protect(string raw) => $"v1.{Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))}";

    private static bool TryUnprotect(string token, out string raw)
    {
        raw = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("v1.", StringComparison.Ordinal)) return false;
        try
        {
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(token[3..]));
            return raw.Length >= 32;
        }
        catch (FormatException) { return false; }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{TokenPurpose}:{value}")));
}
