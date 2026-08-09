namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Invitation temporaire d'un terminal. Le token en clair n'est jamais stocké;
/// seul TokenHash est persistant.
///
/// Deux modes, distingués par <see cref="TerminalId"/> :
///   - Terminal PRÉCRÉÉ (historique, TerminalId non nul) : à USAGE UNIQUE —
///     IsUsable se ferme dès le premier scan (UsedAt), qui lie CE terminal.
///   - Poste (09/08/2026, TerminalId NUL) : ticket RÉUTILISABLE tant que dans
///     sa fenêtre de validité — chaque scan CRÉE un nouveau Terminal à la
///     volée (voir PosteLabel/PosteSiteIds/PosteCheckpointId, le "gabarit"),
///     sans jamais consommer le ticket (UsedAt reste toujours nul pour ce
///     mode). Permet d'enrôler N appareils physiques pour un même poste
///     depuis UN SEUL QR, sans repasser par la console entre chaque appareil.
/// </summary>
public sealed class TerminalEnrollmentTicketEntity
{
    public Guid Id { get; private set; }
    public Guid? TerminalId { get; private set; }
    public string TokenHash { get; private set; } = default!;

    /// <summary>Gabarit du terminal auto-créé à chaque scan — UNIQUEMENT renseigné en mode poste (TerminalId nul).</summary>
    public string? PosteLabel { get; private set; }
    public List<string>? PosteSiteIds { get; private set; }
    public string? PosteCheckpointId { get; private set; }

    /// <summary>
    /// Empreinte du code manuel (ex. « K3XM-7QRT »), généré EN MÊME TEMPS que
    /// le ticket QR et partageant le même cycle de vie (ExpiresAt/UsedAt/
    /// RevokedAt) — une alternative de secours si la caméra du terminal est
    /// hors service, pas un second mécanisme parallèle : scanner le QR ou
    /// saisir le code consomment la MÊME ligne.
    /// </summary>
    public string? ManualCodeHash { get; private set; }
    public string CreatedBy { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? DeviceInstanceId { get; private set; }

    private TerminalEnrollmentTicketEntity() { }

    public static TerminalEnrollmentTicketEntity Create(
        Guid terminalId, string tokenHash, string manualCodeHash, string createdBy,
        DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        TerminalId = terminalId,
        TokenHash = tokenHash,
        ManualCodeHash = manualCodeHash,
        CreatedBy = createdBy,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt,
    };

    public static TerminalEnrollmentTicketEntity CreateForPoste(
        string posteLabel, IReadOnlyList<string> posteSiteIds, string? posteCheckpointId,
        string tokenHash, string manualCodeHash, string createdBy,
        DateTimeOffset createdAt, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        TerminalId = null,
        PosteLabel = posteLabel,
        PosteSiteIds = posteSiteIds.ToList(),
        PosteCheckpointId = posteCheckpointId,
        TokenHash = tokenHash,
        ManualCodeHash = manualCodeHash,
        CreatedBy = createdBy,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt,
    };

    /// <summary>Mode poste (TerminalId nul) : réutilisable, UsedAt n'entre jamais en jeu (voir Consume).</summary>
    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && now < ExpiresAt && (TerminalId is null || UsedAt is null);

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    /// <summary>
    /// Ferme DÉFINITIVEMENT le ticket (mode terminal précréé : usage unique
    /// normal). En mode poste, ne JAMAIS appeler ceci — le ticket doit rester
    /// scannable par d'autres appareils jusqu'à expiration ; le device qui
    /// vient de rejoindre est tracé sur le NOUVEAU Terminal créé, pas ici.
    /// </summary>
    public void Consume(DateTimeOffset now, string deviceInstanceId)
    {
        UsedAt = now;
        DeviceInstanceId = deviceInstanceId;
    }
}