namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Terminal agent enrôlé. La clé API n'est jamais stockée en clair, seul son
/// hash SHA-256 est conservé. L'appareil est lié à l'installation via son
/// identifiant et sa clé publique enregistrée lors de l'activation QR.
/// </summary>
public sealed class Terminal
{
    public Guid Id { get; private set; }
    public string Label { get; private set; } = default!;
    public string ApiKeyHash { get; private set; } = default!;
    public List<string> SiteIds { get; private set; } = new();
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? DeviceInstanceId { get; private set; }
    public string? DevicePublicKeyPem { get; private set; }
    public DateTimeOffset? EnrolledAt { get; private set; }
    public bool IsEnrolled => !string.IsNullOrWhiteSpace(DeviceInstanceId) && EnrolledAt.HasValue;

    /// <summary>
    /// Suppression logique (archivage) : distincte de <see cref="IsActive"/>.
    /// Un terminal révoqué reste visible et son historique consultable ; un
    /// terminal supprimé disparaît des listes normales mais la ligne reste en
    /// base. Son DeviceInstanceId redevient disponible (un appareil physique
    /// peut être réenrôlé comme nouveau terminal) — voir l'index unique
    /// partiel dans NovAccesIdentityDbContext.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; private set; }
    public string? DeletedBy { get; private set; }

    /// <summary>
    /// Poste actif de CE terminal (un seul à la fois — démarrer un nouveau
    /// poste remplace silencieusement le précédent, comme un agent qui prend
    /// la relève sans que le précédent ait clôturé). Le Jti sert à distinguer
    /// « ce jeton de poste est toujours celui en cours » de « ce jeton reste
    /// cryptographiquement valide mais son poste a été clos » — POST
    /// /api/agent/shift/end clôt sans révoquer le JWT lui-même (stateless),
    /// il retire seulement le terminal de la liste des porteurs reconnus pour
    /// l'attribution des scans (ResolveAgentId retombe alors sur le terminal).
    /// </summary>
    public string? ActiveShiftJti { get; private set; }
    public string? ActiveShiftMatricule { get; private set; }
    public DateTimeOffset? ActiveShiftStartedAt { get; private set; }

    /// <summary>
    /// Jeton de notification push Expo de CE terminal (§7 : alerte de
    /// dépassement même app fermée). Remis à jour à chaque prise de poste —
    /// l'app le régénère si réinstallée, un jeton périmé est simplement
    /// écrasé par le suivant, jamais accumulé.
    /// </summary>
    public string? ExpoPushToken { get; private set; }

    private Terminal() { }

    public static Terminal Create(string label, string apiKeyHash, IReadOnlyList<string> siteIds, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        ApiKeyHash = apiKeyHash,
        SiteIds = siteIds.ToList(),
        IsActive = true,
        CreatedAt = now,
    };

    public void BindDevice(string deviceInstanceId, string devicePublicKeyPem, string apiKeyHash, DateTimeOffset now)
    {
        DeviceInstanceId = deviceInstanceId;
        DevicePublicKeyPem = devicePublicKeyPem;
        ApiKeyHash = apiKeyHash;
        EnrolledAt = now;
        IsActive = true;
        RevokedAt = null;
    }

    public void Revoke(DateTimeOffset now)
    {
        IsActive = false;
        RevokedAt = now;
    }

    /// <summary>
    /// Suppression logique : n'est permise que sur un terminal déjà révoqué
    /// (discipline en deux temps, comme pour un agent, un compte ou un site).
    /// </summary>
    public void Delete(DateTimeOffset now, string actor)
    {
        if (IsActive)
            throw new InvalidOperationException("Révoquez le terminal avant de le supprimer.");

        DeletedAt = now;
        DeletedBy = actor;
    }

    /// <summary>Prise de poste : remplace le poste actif précédent, s'il y en avait un.</summary>
    public void StartShift(string jti, string matricule, DateTimeOffset now)
    {
        ActiveShiftJti = jti;
        ActiveShiftMatricule = matricule;
        ActiveShiftStartedAt = now;
    }

    /// <summary>
    /// Fin de poste idempotente : n'a d'effet que si le jeton présenté est
    /// bien celui du poste en cours. Rejouer la clôture, ou la présenter
    /// après qu'un autre agent a démarré un nouveau poste, ne fait rien —
    /// jamais d'erreur, jamais de clôture d'un poste qui n'est pas le sien.
    /// </summary>
    public void EndShift(string jti, DateTimeOffset now)
    {
        if (!string.Equals(ActiveShiftJti, jti, StringComparison.Ordinal))
            return;

        ActiveShiftJti = null;
        ActiveShiftMatricule = null;
        ActiveShiftStartedAt = null;
    }

    public bool IsShiftActive(string jti) =>
        !string.IsNullOrEmpty(jti) && string.Equals(ActiveShiftJti, jti, StringComparison.Ordinal);

    /// <summary>Enregistre (ou efface, si null/vide) le jeton push Expo courant de ce terminal.</summary>
    public void SetExpoPushToken(string? token) =>
        ExpoPushToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
}