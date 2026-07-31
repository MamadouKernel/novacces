using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Positionne le search_path PostgreSQL sur le schéma du tenant courant à
/// CHAQUE ouverture de connexion physique (REQ-F-10 : cloisonnement strict).
///
/// Pourquoi un intercepteur et pas un simple "SET search_path" avant la requête :
/// Npgsql met les connexions en pool et RÉINITIALISE leur état de session
/// (dont search_path) quand elles retournent au pool. Un "SET" exécuté sur une
/// connexion, suivi d'une requête EF qui obtient une AUTRE connexion du pool
/// (ou la même après réinitialisation), s'exécuterait alors dans le schéma par
/// défaut (public) — donc hors du tenant. En agissant sur l'événement
/// "connexion ouverte", on garantit que TOUTE connexion servie par EF Core,
/// pour n'importe quelle requête, est cantonnée au bon schéma avant le premier
/// octet de SQL métier.
///
/// Le nom de schéma provient exclusivement de CurrentTenant.SchemaName, dérivé
/// d'un identifiant validé par CurrentTenant.IsValidSiteId (whitelist ASCII
/// stricte, longueur bornée). Il est en outre mis entre guillemets ici, en
/// défense en profondeur (PostgreSQL ne paramètre pas les identifiants).
///
/// ZONE SENSIBLE — voir CLAUDE.md §7.3. Toute modification doit être relue
/// humainement : une erreur ici est une fuite de données entre clients.
/// </summary>
public sealed class TenantSchemaConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ICurrentTenant _tenant;

    public TenantSchemaConnectionInterceptor(ICurrentTenant tenant) => _tenant = tenant;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!_tenant.IsResolved)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = BuildSetSearchPath();
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!_tenant.IsResolved)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = BuildSetSearchPath();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Le schéma est déjà validé et assaini en amont (CurrentTenant.IsValidSiteId) ;
    // les guillemets sont une ceinture de sécurité supplémentaire.
    private string BuildSetSearchPath() => $"SET search_path TO \"{_tenant.SchemaName}\", public";
}
