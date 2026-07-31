namespace NovAcces.Application.Abstractions;

public interface IRefreshTokenService
{
    Task<RefreshTokenIssued> IssueAsync(string subjectType, string subjectId, string? displayName, string? siteId, CancellationToken ct);
    Task<RefreshTokenSubject?> RotateAsync(string refreshToken, CancellationToken ct);
    Task RevokeAsync(string refreshToken, CancellationToken ct);
    Task RevokeAllForSubjectAsync(string subjectType, string subjectId, CancellationToken ct);
}

public sealed record RefreshTokenIssued(string Token, DateTimeOffset ExpiresAt);
public sealed record RefreshTokenSubject(string SubjectType, string SubjectId, string? DisplayName, string? SiteId);
