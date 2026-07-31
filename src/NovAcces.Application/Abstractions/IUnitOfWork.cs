namespace NovAcces.Application.Abstractions;

/// <summary>
/// Frontière transactionnelle explicite. Exécute une opération dans une
/// transaction base de données unique : commit si elle réussit, rollback si
/// elle lève.
///
/// Raison d'être — REQ-SEC-03 (anti-rejeu) : le verrou pessimiste posé par
/// IVisitRepository.GetForUpdateAsync (SELECT … FOR UPDATE) n'est tenu que
/// pour la durée de la transaction courante. Sans transaction englobante, le
/// verrou serait relâché dès la fin de la requête de lecture, AVANT la
/// sauvegarde — deux scans simultanés du même QR pourraient alors passer.
/// Envelopper « lecture verrouillée → application de la règle → sauvegarde »
/// dans une seule transaction est ce qui rend le verrou réellement protecteur.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
}
