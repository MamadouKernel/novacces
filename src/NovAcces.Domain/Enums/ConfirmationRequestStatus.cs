namespace NovAcces.Domain.Enums;

/// <summary>
/// Cycle de vie d'une demande de confirmation sûreté (validation sans QR/code
/// depuis la liste « Attendus » de l'app agent) — voir ScanConfirmationRequest.
/// </summary>
public enum ConfirmationRequestStatus
{
    Pending,
    Approved,
    Denied,

    /// <summary>Aucune décision de la sûreté avant l'expiration du délai — équivaut à un refus implicite (décision du 08/08/2026).</summary>
    Expired
}
