using NovAcces.Application.Abstractions;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Sauvegarde de la base de données COMPLÈTE (tous les sites + schéma
/// partagé) — réservée au SuperAdmin, strictement plus étroit que le reste
/// de la console d'administration (policy Admin) : une sauvegarde complète
/// expose en un seul fichier les données de TOUS les clients de
/// Sigasécurité, pas seulement celles d'un site. Chaque appel est déjà
/// tracé par le journal technique global (ApplicationAuditMiddleware,
/// actor/méthode/chemin/statut/IP/horodatage) — pas de journal dédié
/// supplémentaire ici, cette action n'est pas rattachable à UN site (donc
/// pas au journal admin_audit, qui vit dans le schéma de chaque site).
///
/// Volontairement AUCUN endpoint de restauration : voir
/// IDatabaseBackupService pour le raisonnement (action destructrice, hors
/// de portée d'un bouton web sans garde-fou dédié).
/// </summary>
public static class DatabaseAdminEndpoints
{
    public static RouteGroupBuilder MapDatabaseAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/database").WithTags("Admin — Base de données")
            .RequireAuthorization(NovAccesRoles.SuperAdmin);

        group.MapPost("/backups", async (IDatabaseBackupService backups, CancellationToken ct) =>
        {
            try
            {
                var backup = await backups.CreateBackupAsync(ct);
                return Results.Ok(ToDto(backup));
            }
            catch (DatabaseBackupInProgressException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (DatabaseBackupFailedException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("CreateDatabaseBackup")
        .WithSummary("Déclenche une sauvegarde immédiate de la base complète (pg_dump, format personnalisé).")
        .Produces<DatabaseBackupDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status500InternalServerError);

        group.MapGet("/backups", async (IDatabaseBackupService backups, CancellationToken ct) =>
        {
            var list = await backups.ListBackupsAsync(ct);
            return Results.Ok(list.Select(ToDto).ToList());
        })
        .WithName("ListDatabaseBackups")
        .WithSummary("Liste les sauvegardes présentes sur le volume, les plus récentes d'abord.")
        .Produces<List<DatabaseBackupDto>>(StatusCodes.Status200OK);

        group.MapGet("/backups/{fileName}/download", async (
            string fileName, IDatabaseBackupService backups, CancellationToken ct) =>
        {
            var stream = await backups.OpenBackupForDownloadAsync(fileName, ct);
            return stream is null
                ? Results.NotFound(new { error = "Sauvegarde introuvable." })
                : Results.File(stream, "application/octet-stream", fileName);
        })
        .WithName("DownloadDatabaseBackup")
        .WithSummary("Télécharge le fichier d'une sauvegarde existante.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static DatabaseBackupDto ToDto(DatabaseBackupInfo info) =>
        new(info.FileName, info.SizeBytes, info.CreatedAt);
}
