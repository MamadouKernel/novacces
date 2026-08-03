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
}
