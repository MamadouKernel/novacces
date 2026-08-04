namespace NovAcces.Domain.Entities;

/// <summary>
/// Agent de contrôle d'accès d'un site (données par tenant). Sert à la
/// « prise de poste » : l'agent s'identifie sur le terminal par matricule + PIN,
/// vérifié côté serveur, pour que chaque scan soit tracé à SON matricule
/// (traçabilité individuelle validée par la maquette, §8.5).
///
/// Le PIN n'est jamais stocké en clair : seul son empreinte salée (PinHash) est
/// conservée (voir IAgentDirectory / AgentDirectory).
/// </summary>
public sealed class Agent
{
    public Guid Id { get; private set; }

    /// <summary>Matricule de l'agent (identifiant métier, ex. « SG-0417 »).</summary>
    public string Matricule { get; private set; } = default!;

    public string DisplayName { get; private set; } = default!;

    /// <summary>Empreinte salée du PIN (jamais le PIN en clair).</summary>
    public string PinHash { get; private set; } = default!;

    /// <summary>Un agent désactivé ne peut plus prendre de poste (départ, suspension).</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Suppression logique (§ Archivés) : distincte de <see cref="IsActive"/>.
    /// Un agent désactivé reste visible et réactivable ; un agent supprimé
    /// disparaît des listes normales mais la ligne reste en base (traçabilité
    /// — les journaux de scan référencent le matricule en texte, pas une clé
    /// étrangère, donc rien ne dépend de la présence de cette ligne). Son
    /// matricule redevient disponible pour un NOUVEL agent (voir l'index
    /// unique partiel dans AgentConfiguration).
    /// </summary>
    public DateTimeOffset? DeletedAt { get; private set; }
    public string? DeletedBy { get; private set; }

    /// <summary>Nombre d'échecs PIN depuis la dernière authentification réussie.</summary>
    public int FailedPinAttempts { get; private set; }

    /// <summary>Fin du verrouillage temporaire après trop d'échecs PIN.</summary>
    public DateTimeOffset? PinLockoutEnd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private Agent() { } // EF Core

    public static Agent Create(string matricule, string displayName, string pinHash, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Matricule = matricule.Trim(),
        DisplayName = displayName.Trim(),
        PinHash = pinHash,
        IsActive = true,
        CreatedAt = now,
    };

    public void UpdatePin(string pinHash)
    {
        PinHash = pinHash;
        ResetPinFailures();
    }

    public bool IsPinLocked(DateTimeOffset now) =>
        PinLockoutEnd is { } until && until > now;

    public void RegisterFailedPin(DateTimeOffset now, int maxAttempts, TimeSpan lockout)
    {
        if (IsPinLocked(now))
            return;

        FailedPinAttempts++;
        if (FailedPinAttempts >= Math.Max(1, maxAttempts))
        {
            PinLockoutEnd = now.Add(lockout);
            FailedPinAttempts = 0;
        }
    }

    public void ResetPinFailures()
    {
        FailedPinAttempts = 0;
        PinLockoutEnd = null;
    }

    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Rouvre le matricule d'un agent désactivé (retour sur un site quitté).
    /// Le PIN précédent reste valable — la sûreté peut le réinitialiser
    /// séparément si nécessaire, comme pour tout autre agent actif.
    /// </summary>
    public void Reactivate()
    {
        IsActive = true;
        ResetPinFailures();
    }

    /// <summary>
    /// Suppression logique : n'est permise que sur un agent déjà désactivé
    /// (discipline en deux temps, comme la réaffectation) — un agent encore
    /// actif doit d'abord être désactivé, jamais supprimé d'un coup.
    /// </summary>
    public void Delete(DateTimeOffset now, string actor)
    {
        if (IsActive)
            throw new InvalidOperationException("Désactivez l'agent avant de le supprimer.");

        DeletedAt = now;
        DeletedBy = actor;
    }
}
