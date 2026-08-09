using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Entities;
using NovAcces.Domain.Enums;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// Journal d'audit admin (section 8.5) stocké dans le schéma du tenant courant.
/// L'écriture est un simple INSERT ; toute tentative de UPDATE/DELETE est
/// rejetée par le trigger append-only posé au provisionnement (défense au
/// niveau base, indépendante du code applicatif).
/// </summary>
public sealed class AdminAuditLog : IAdminAuditLog
{
    private readonly NovAccesDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly IAdminActivityBroadcaster _broadcaster;
    private readonly ILogger<AdminAuditLog> _logger;

    public AdminAuditLog(
        NovAccesDbContext db, IDateTimeProvider clock,
        IAdminActivityBroadcaster broadcaster, ILogger<AdminAuditLog> logger)
    {
        _db = db;
        _clock = clock;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task RecordAsync(
        AdminAuditAction action, string actor, string? targetId, string detail, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        _db.AdminAudit.Add(AdminAuditEntry.Create(actor, action, targetId, detail, _clock.UtcNow));
        await _db.SaveChangesAsync(ct);

        // Choc unique en écriture pour TOUTE action privilégiée (agent créé,
        // terminal révoqué, exclusion ajoutée, etc.) : un seul signal ici
        // couvre /admin/audit sans avoir à instrumenter chaque appelant.
        // Best-effort — ne doit jamais remettre en cause l'écriture déjà faite.
        try
        {
            await _broadcaster.NotifyEntityChangedAsync("audit", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Échec de diffusion temps réel de l'écriture au journal d'audit.");
        }
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> GetRecentAsync(int limit, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        return await _db.AdminAudit
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<AdminAuditEntry> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken ct)
    {
        await _db.EnsureTenantResolvedAsync(ct);
        var query = _db.AdminAudit.AsNoTracking().OrderByDescending(e => e.Timestamp);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }
}
