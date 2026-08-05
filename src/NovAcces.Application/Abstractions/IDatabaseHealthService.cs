namespace NovAcces.Application.Abstractions;

/// <summary>Diagnostic transverse de la base — réservé au SuperAdmin, comme le reste de l'administration base de données.</summary>
public interface IDatabaseHealthService
{
    Task<DatabaseHealthOverview> GetOverviewAsync(CancellationToken ct);
}

public sealed record DatabaseHealthOverview(
    string PostgresVersion,
    long TotalSizeBytes,
    int ActiveConnections,
    IReadOnlyList<DatabaseSchemaStats> Schemas);

/// <summary>Statistiques d'un schéma (site ou partagé "identity").</summary>
public sealed record DatabaseSchemaStats(string SchemaName, long SizeBytes, int TableCount, long ApproximateRowCount);
