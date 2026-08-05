using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Annuaire partagé des terminaux. Le parcours de production crée un ticket
/// temporaire, puis ne remet la nouvelle clé API qu'après activation du mobile.
/// </summary>
public sealed class TerminalDirectory : ITerminalDirectory
{
    private readonly NovAccesIdentityDbContext _db;

    public TerminalDirectory(NovAccesIdentityDbContext db) => _db = db;

    public static string ComputeKeyHash(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    private static string GenerateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task<TerminalIdentity?> VerifyAsync(string presentedApiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedApiKey))
            return null;

        var hash = ComputeKeyHash(presentedApiKey);
        var terminal = await _db.Terminals.FirstOrDefaultAsync(
            t => t.ApiKeyHash == hash && t.IsActive && t.DeviceInstanceId != null && t.EnrolledAt != null, ct);

        return terminal is null ? null : new TerminalIdentity(terminal.Id, terminal.Label, terminal.SiteIds);
    }

    public async Task<Guid> CreateAsync(
        string label, IReadOnlyList<string> siteIds, CancellationToken ct)
    {
        var reservedHash = ComputeKeyHash(GenerateSecret());
        var terminal = Terminal.Create(label, reservedHash, siteIds, DateTimeOffset.UtcNow);

        _db.Terminals.Add(terminal);
        await _db.SaveChangesAsync(ct);

        return terminal.Id;
    }

    public async Task<IReadOnlyList<TerminalSummary>> ListAsync(CancellationToken ct) =>
        await _db.Terminals
            .Where(t => t.DeletedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TerminalSummary(t.Id, t.Label, t.SiteIds, t.IsActive, t.CreatedAt, t.IsEnrolled))
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct)
    {
        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (terminal is null)
            return false;

        var now = DateTimeOffset.UtcNow;
        terminal.Revoke(now);
        var pendingTickets = await _db.TerminalEnrollmentTickets
            .Where(t => t.TerminalId == id && t.UsedAt == null && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var pending in pendingTickets)
            pending.Revoke(now);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, string actor, CancellationToken ct)
    {
        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null, ct);
        if (terminal is null)
            return false;

        terminal.Delete(DateTimeOffset.UtcNow, actor);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ArchivedTerminalSummary>> ListArchivedAsync(CancellationToken ct) =>
        await _db.Terminals
            .Where(t => t.DeletedAt != null)
            .OrderByDescending(t => t.DeletedAt)
            .Select(t => new ArchivedTerminalSummary(t.Id, t.Label, t.SiteIds, t.DeletedAt!.Value, t.DeletedBy))
            .ToListAsync(ct);

    public async Task<TerminalEnrollmentTicket?> CreateEnrollmentTicketAsync(
        Guid terminalId, string createdBy, TimeSpan lifetime, CancellationToken ct)
    {
        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct);
        if (terminal is null || !terminal.IsActive)
            return null;

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(lifetime);
        var rawTicket = GenerateSecret();

        // Un nouveau QR invalide les invitations précédentes du même terminal.
        var pending = await _db.TerminalEnrollmentTickets
            .Where(t => t.TerminalId == terminalId && t.UsedAt == null && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var old in pending)
            old.Revoke(now);

        _db.TerminalEnrollmentTickets.Add(TerminalEnrollmentTicketEntity.Create(
            terminalId, ComputeKeyHash(rawTicket), createdBy, now, expiresAt));
        await _db.SaveChangesAsync(ct);

        return new TerminalEnrollmentTicket(
            terminal.Id, terminal.Label, terminal.SiteIds.ToList(), rawTicket, expiresAt);
    }

    public async Task<TerminalActivation?> ActivateAsync(
        string ticket, string deviceInstanceId, string devicePublicKeyPem, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(deviceInstanceId)
            || string.IsNullOrWhiteSpace(devicePublicKeyPem))
            return null;

        // Serializable + relecture dans la transaction : deux téléphones qui
        // scanneraient le même QR ne peuvent pas tous les deux l'activer.
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = DateTimeOffset.UtcNow;
        var ticketHash = ComputeKeyHash(ticket.Trim());
        var ticketEntity = await _db.TerminalEnrollmentTickets
            .FirstOrDefaultAsync(t => t.TokenHash == ticketHash, ct);
        if (ticketEntity is null || !ticketEntity.IsUsable(now))
            return null;

        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Id == ticketEntity.TerminalId, ct);
        if (terminal is null || !terminal.IsActive)
            return null;

        var apiKey = GenerateSecret();
        terminal.BindDevice(deviceInstanceId.Trim(), devicePublicKeyPem.Trim(), ComputeKeyHash(apiKey), now);
        ticketEntity.Consume(now, deviceInstanceId.Trim());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new TerminalActivation(
            terminal.Id, terminal.Label, terminal.SiteIds.ToList(), apiKey, now);
    }

    public async Task SetActiveShiftAsync(Guid terminalId, string shiftJti, string matricule, DateTimeOffset now, CancellationToken ct)
    {
        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct);
        if (terminal is null)
            return;

        terminal.StartShift(shiftJti, matricule, now);
        await _db.SaveChangesAsync(ct);
    }

    public async Task EndActiveShiftAsync(Guid terminalId, string shiftJti, DateTimeOffset now, CancellationToken ct)
    {
        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct);
        if (terminal is null)
            return;

        terminal.EndShift(shiftJti, now);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsShiftActiveAsync(Guid terminalId, string shiftJti, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(shiftJti))
            return false;

        return await _db.Terminals.AsNoTracking()
            .AnyAsync(t => t.Id == terminalId && t.ActiveShiftJti == shiftJti, ct);
    }
}