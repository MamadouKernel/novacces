namespace NovAcces.Domain.Enums;

/// <summary>
/// Mode de validité d'une visite, conformément à REQ-F-05 du CDC.
/// </summary>
public enum AccessMode
{
    /// <summary>Fenêtre stricte -20 min / +15 min autour du rendez-vous.</summary>
    Unique = 0,

    /// <summary>Accès sur 30 jours, jours ouvrés uniquement.</summary>
    ThirtyDays = 1
}
