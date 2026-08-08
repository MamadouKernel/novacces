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
    DashboardOverride = 2,

    /// <summary>
    /// Accès accordé sans QR ni code, sur confirmation explicite de la sûreté
    /// depuis le portail Web (liste « Attendus » de l'app agent) — voir
    /// ScanConfirmationRequest. La revérification complète (anti-rejeu,
    /// exclusion, fenêtre) a quand même eu lieu au moment de l'approbation,
    /// via ScanExecutionCore — seule la PREUVE d'identité diffère d'un QR/code.
    /// </summary>
    SureteConfirmation = 3
}
