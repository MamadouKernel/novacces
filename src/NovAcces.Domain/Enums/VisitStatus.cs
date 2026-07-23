namespace NovAcces.Domain.Enums;

public enum VisitStatus
{
    /// <summary>QR généré, aucune entrée enregistrée.</summary>
    Valid = 0,

    /// <summary>Entrée consommée (mode Unique) ou cycle en cours (mode ThirtyDays).</summary>
    Consumed = 1,

    /// <summary>Révoqué manuellement par l'hôte ou la sûreté.</summary>
    Revoked = 2,

    /// <summary>Fenêtre de validité définitivement dépassée sans qu'aucun scan n'ait eu lieu (mode Unique).</summary>
    Expired = 3
}
