namespace NovAcces.Domain.Enums;

/// <summary>
/// Comment un scan a été authentifié — distinction utile pour la lecture du
/// journal par la sûreté (un code de secours est un moyen d'accès plus
/// faible qu'un QR signé ES256, bon à savoir en cas d'audit). DashboardOverride
/// couvre la sortie manuelle sans scan (voir DashboardEndpoints, ForceCheckOut).
/// </summary>
public enum ScanAuthMethod
{
    Qr = 0,
    ManualCode = 1,
    DashboardOverride = 2
}
