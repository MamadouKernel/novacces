using NovAcces.Shared.Dtos;
using SQLite;

namespace NovAcces.Mobile.Services;

/// <summary>
/// Persistance locale (SQLite) des scans réalisés hors ligne, en attente de
/// resynchronisation. Elle DOIT survivre à un redémarrage de l'app pendant une
/// coupure réseau (§6.5) — d'où le stockage disque et non une simple liste
/// mémoire. La resynchronisation vide la file une fois les scans confrontés au
/// registre central.
/// </summary>
public sealed class OfflineScanStore
{
    private readonly SQLiteAsyncConnection _db;
    private bool _ready;

    public OfflineScanStore(string databasePath)
        => _db = new SQLiteAsyncConnection(databasePath);

    private async Task EnsureReadyAsync()
    {
        if (_ready) return;
        await _db.CreateTableAsync<PendingScanRow>();
        _ready = true;
    }

    public async Task EnqueueAsync(OfflineScanDto scan)
    {
        await EnsureReadyAsync();
        await _db.InsertAsync(PendingScanRow.From(scan));
    }

    public async Task<IReadOnlyList<OfflineScanDto>> GetAllAsync()
    {
        await EnsureReadyAsync();
        var rows = await _db.Table<PendingScanRow>().OrderBy(r => r.OccurredAtUnix).ToListAsync();
        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task ClearAsync()
    {
        await EnsureReadyAsync();
        await _db.DeleteAllAsync<PendingScanRow>();
    }

    public async Task<int> CountAsync()
    {
        await EnsureReadyAsync();
        return await _db.Table<PendingScanRow>().CountAsync();
    }
}

/// <summary>Ligne persistée d'un scan hors-ligne (mappée depuis/vers OfflineScanDto).</summary>
[Table("pending_offline_scans")]
public sealed class PendingScanRow
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public string VisitToken { get; set; } = "";
    public string Direction { get; set; } = "";
    public bool WasGranted { get; set; }
    public long OccurredAtUnix { get; set; }
    public string? VerdictCode { get; set; }
    public bool WasSecurityEvent { get; set; }

    public static PendingScanRow From(OfflineScanDto s) => new()
    {
        VisitToken = s.VisitToken.ToString(),
        Direction = s.Direction,
        WasGranted = s.WasGranted,
        OccurredAtUnix = s.OccurredAt.ToUnixTimeMilliseconds(),
        VerdictCode = s.VerdictCode,
        WasSecurityEvent = s.WasSecurityEvent,
    };

    public OfflineScanDto ToDto() => new(
        Guid.Parse(VisitToken),
        Direction,
        WasGranted,
        DateTimeOffset.FromUnixTimeMilliseconds(OccurredAtUnix),
        VerdictCode,
        WasSecurityEvent);
}
