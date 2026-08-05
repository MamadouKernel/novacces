using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence.Tenancy;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// Console SQL en lecture seule pour le SuperAdmin. Trois barrières
/// indépendantes, pas une seule — la validation de texte est la plus
/// faible des trois et n'est là qu'en première ligne :
///
///  1. Une seule instruction (rejet de tout ';' interne) — empêche
///     d'empiler une seconde commande derrière un SELECT innocent.
///  2. La requête est enveloppée dans "SELECT * FROM (...) AS q LIMIT N" —
///     ceci échoue à la analyse syntaxique PostgreSQL pour toute requête qui
///     n'est pas elle-même un SELECT (on ne peut pas mettre un DELETE/UPDATE
///     dans une sous-requête), et borne le nombre de lignes renvoyées.
///  3. Exécution dans une transaction PostgreSQL READ ONLY — la vraie
///     garantie : même une barrière 1 et 2 contournées par une construction
///     exotique, PostgreSQL lui-même refuse toute écriture dans cette
///     transaction, quel que soit le rôle utilisé.
///
/// statement_timeout borné : une requête coûteuse ne doit pas monopoliser
/// une connexion indéfiniment.
/// </summary>
public sealed class PostgresReadOnlyQueryService : IDatabaseQueryService
{
    private const int MaxRows = 500;
    private const int StatementTimeoutSeconds = 10;

    private readonly string _connectionString;
    private readonly ILogger<PostgresReadOnlyQueryService> _logger;

    public PostgresReadOnlyQueryService(IConfiguration configuration, ILogger<PostgresReadOnlyQueryService> logger)
    {
        _connectionString = PostgresAdminConnectionResolver.Resolve(configuration);
        _logger = logger;
    }

    /// <summary>
    /// Barrière 1 (la plus faible des trois, voir la remarque de classe) :
    /// tolère un ';' final (habitude d'écriture SQL) mais rejette tout ';'
    /// interne — signe d'une seconde instruction empilée derrière un SELECT
    /// innocent. Pure et testable sans connexion base (voir
    /// PostgresReadOnlyQueryServiceTests).
    /// </summary>
    public static string NormalizeSingleStatement(string? sql)
    {
        var trimmed = (sql ?? "").Trim();
        if (trimmed.Length == 0)
            throw new InvalidReadOnlyQueryException("Requête vide.");

        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();
        if (trimmed.Contains(';'))
            throw new InvalidReadOnlyQueryException("Une seule instruction à la fois (pas de ';' interne).");

        return trimmed;
    }

    public async Task<DatabaseQueryResult> ExecuteReadOnlyAsync(string sql, CancellationToken ct)
    {
        var trimmed = NormalizeSingleStatement(sql);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        // Barrière 3 (la vraie) : PostgreSQL refuse toute écriture dans cette
        // transaction, indépendamment de ce que la requête essaierait de faire.
        await using (var setReadOnly = connection.CreateCommand())
        {
            setReadOnly.Transaction = transaction;
            setReadOnly.CommandText = $"SET LOCAL statement_timeout = '{StatementTimeoutSeconds}s'; SET TRANSACTION READ ONLY;";
            await setReadOnly.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Barrière 2 : n'importe quoi d'autre qu'un SELECT échoue ici à
        // l'analyse syntaxique PostgreSQL, avant même l'exécution.
        command.CommandText = $"SELECT * FROM ({trimmed}) AS admin_query LIMIT {MaxRows + 1}";

        DatabaseQueryResult result;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<string?>>();
            while (await reader.ReadAsync(ct) && rows.Count < MaxRows)
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
                rows.Add(row);
            }

            // Une ligne de plus que MaxRows a été demandée (LIMIT MaxRows+1) :
            // si elle est arrivée jusqu'ici, le résultat réel était plus grand.
            var truncated = rows.Count >= MaxRows && await reader.ReadAsync(ct);
            result = new DatabaseQueryResult(columns, rows, truncated);
        }
        catch (PostgresException ex)
        {
            throw new InvalidReadOnlyQueryException($"Requête refusée par PostgreSQL : {ex.MessageText}");
        }
        finally
        {
            await transaction.RollbackAsync(ct);
        }

        return result;
    }
}
