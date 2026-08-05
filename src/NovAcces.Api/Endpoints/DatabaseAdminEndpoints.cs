using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Administration transverse de la base de données COMPLÈTE (tous les sites
/// + schéma partagé) — réservée au SuperAdmin, strictement plus étroit que
/// le reste de la console d'administration (policy Admin) : ces opérations
/// exposent ou touchent en un coup les données de TOUS les clients de
/// Sigasécurité, pas seulement celles d'un site. Chaque appel est déjà
/// tracé par le journal technique global (ApplicationAuditMiddleware,
/// actor/méthode/chemin/statut/IP/horodatage) — pas de journal dédié
/// supplémentaire ici, cette action n'est pas rattachable à UN site (donc
/// pas au journal admin_audit, qui vit dans le schéma de chaque site) ; la
/// console SQL (ci-dessous) journalise en plus le texte de chaque requête.
///
/// Volontairement AUCUN endpoint de restauration : voir
/// IDatabaseBackupService pour le raisonnement (action destructrice, hors
/// de portée d'un bouton web sans garde-fou dédié — CLI uniquement).
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

        group.MapGet("/health", async (IDatabaseHealthService health, CancellationToken ct) =>
        {
            var overview = await health.GetOverviewAsync(ct);
            return Results.Ok(new DatabaseHealthDto(
                overview.PostgresVersion, overview.TotalSizeBytes, overview.ActiveConnections,
                overview.Schemas.Select(s => new DatabaseSchemaStatsDto(
                    s.SchemaName, s.SizeBytes, s.TableCount, s.ApproximateRowCount)).ToList()));
        })
        .WithName("GetDatabaseHealth")
        .WithSummary("Diagnostic transverse : version PostgreSQL, taille, connexions actives, statistiques par schéma.")
        .Produces<DatabaseHealthDto>(StatusCodes.Status200OK);

        group.MapPost("/query", async (
            DatabaseQueryRequestDto request, IDatabaseQueryService query, ClaimsPrincipal caller,
            ILogger<Program> logger, CancellationToken ct) =>
        {
            // Journalisation explicite du texte de la requête : cette console
            // touche potentiellement les données de tous les sites, la trace
            // générique (ApplicationAuditMiddleware) n'enregistre pas le corps
            // de la requête HTTP.
            logger.LogInformation(
                "Console SQL (lecture seule) exécutée par {Actor} : {Sql}",
                caller.FindFirstValue(ClaimTypes.Email) ?? caller.Identity?.Name ?? "inconnu", request.Sql);

            try
            {
                var result = await query.ExecuteReadOnlyAsync(request.Sql, ct);
                return Results.Ok(new DatabaseQueryResultDto(result.Columns, result.Rows, result.Truncated));
            }
            catch (InvalidReadOnlyQueryException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ExecuteDatabaseQuery")
        .WithSummary("Exécute une requête SQL en lecture seule (SELECT uniquement, une instruction, 500 lignes max).")
        .Produces<DatabaseQueryResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/roles/sync", async (
            ITenantProvisioningService provisioner, CancellationToken ct) =>
        {
            // La CRÉATION des rôles novacces_owner/novacces_app exige un
            // superutilisateur PostgreSQL (voir tools/provisionner-roles-postgres.sql)
            // — geste manuel, une fois, jamais depuis l'application (l'API ne
            // détient et ne doit jamais détenir de connexion superutilisateur).
            // Ce que CET endpoint fait : (re)synchroniser les PRIVILÈGES du
            // rôle applicatif une fois qu'il existe déjà — même opération que
            // `dotnet NovAcces.Api.dll grant-app-role` en CLI.
            if (provisioner is not TenantProvisioningService concrete)
                return Results.Json(
                    new { error = "Service de provisionnement indisponible." },
                    statusCode: StatusCodes.Status500InternalServerError);

            try
            {
                await concrete.ApplyApplicationRoleGrantsEverywhereAsync(ct);
                return Results.Ok(new { message = "Privilèges du rôle applicatif synchronisés (schéma partagé + tous les sites)." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("SyncApplicationRoleGrants")
        .WithSummary("(Re)synchronise les privilèges du rôle applicatif PostgreSQL (Database:ApplicationRole) sur tous les schémas.")
        .WithDescription(
            "Ne CRÉE PAS les rôles PostgreSQL — voir tools/provisionner-roles-postgres.sql (geste manuel, superutilisateur, une fois).")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status500InternalServerError);

        return group;
    }

    private static DatabaseBackupDto ToDto(DatabaseBackupInfo info) =>
        new(info.FileName, info.SizeBytes, info.CreatedAt);
}
