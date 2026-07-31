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

/// <summary>Clé publique ES256 utilisable avant toute authentification.</summary>
public sealed record PublicKeyDto(string Kid, string PublicKeyPem);
