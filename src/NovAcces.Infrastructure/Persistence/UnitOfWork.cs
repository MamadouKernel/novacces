using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence;

/// <summary>
/// Implémentation EF Core de la frontière transactionnelle. Utilise le même
/// DbContext scoped que les dépôts (une instance par requête) : la transaction
/// ouverte ici englobe donc bien leurs opérations, sur une CONNEXION UNIQUE.
///
/// C'est essentiel pour l'anti-rejeu (REQ-SEC-03, CLAUDE.md §7.1) : la
/// transaction épingle la connexion, si bien que le verrou « FOR UPDATE » posé
/// par GetForUpdateAsync est tenu jusqu'au commit, couvrant la sauvegarde.
/// L'intercepteur de tenant positionne le search_path à l'ouverture de cette
/// connexion, donc la transaction reste cantonnée au bon schéma.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly NovAccesDbContext _db;

    public UnitOfWork(NovAccesDbContext db) => _db = db;

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await operation(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
