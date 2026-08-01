namespace NovAcces.Shared.Dtos;

/// <summary>Configuration consommée par l'application agent.</summary>
public sealed record SiteConfigDto(
    string SiteLabel,
    IReadOnlyList<CheckpointDto> Postes,
    SiteParametersDto Params);

public sealed record CheckpointDto(string Id, string Nom);

public sealed record SiteParametersDto(
    int FenetreAvantMin,
    int FenetreApresMin,
    int TtlListeLocaleHeures);

/// <summary>
/// Clé publique ES256 utilisable avant toute authentification. Pendant une
/// rotation, <see cref="RetiredKeys"/> porte les anciennes clés encore acceptées
/// en vérification : le terminal doit les conserver pour pouvoir valider hors
/// ligne les QR longue durée émis avant la bascule.
/// </summary>
public sealed record PublicKeyDto(
    string Kid,
    string PublicKeyPem,
    IReadOnlyList<PublicKeyEntryDto>? RetiredKeys = null);

public sealed record PublicKeyEntryDto(string Kid, string PublicKeyPem);
