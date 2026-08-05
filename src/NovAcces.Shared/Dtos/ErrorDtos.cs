namespace NovAcces.Shared.Dtos;

/// <summary>
/// Forme des réponses d'erreur métier (400/401/403/404/409/410). Sert
/// uniquement à documenter le contrat dans Swagger — les endpoints renvoient
/// eux-mêmes des objets anonymes de cette forme (Results.BadRequest(new
/// { error = "..." }), etc.), ce type n'est jamais construit au runtime.
/// </summary>
public sealed record ErrorResponseDto(string Error, IReadOnlyList<string>? Details = null);
