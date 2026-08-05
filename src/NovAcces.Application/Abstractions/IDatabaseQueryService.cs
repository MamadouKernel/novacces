namespace NovAcces.Application.Abstractions;

/// <summary>
/// Console SQL en LECTURE SEULE pour le SuperAdmin — diagnostic ad hoc sans
/// passer par une action métier structurée. Volontairement pas d'écriture :
/// un UPDATE/DELETE manuel contournerait toute la logique de sûreté du
/// domaine (anti-rejeu, cycle entrée/sortie — Visit.cs). Voir
/// PostgresReadOnlyQueryService pour les barrières concrètes (transaction
/// READ ONLY côté PostgreSQL, pas seulement une validation de texte).
/// </summary>
public interface IDatabaseQueryService
{
    Task<DatabaseQueryResult> ExecuteReadOnlyAsync(string sql, CancellationToken ct);
}

public sealed record DatabaseQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated);

/// <summary>Levée quand la requête soumise n'est pas une simple lecture (autre chose qu'un SELECT, plusieurs instructions, etc.).</summary>
public sealed class InvalidReadOnlyQueryException : Exception
{
    public InvalidReadOnlyQueryException(string message) : base(message) { }
}
