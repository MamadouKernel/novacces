using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Auth;

namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Voir ISiteDataResetService pour le périmètre. Deux connexions distinctes,
/// comme TenantProvisioningService : le DROP/CREATE de schéma passe par la
/// connexion PROPRIÉTAIRE (raw NpgsqlConnection), la purge du schéma partagé
/// "identity" passe par le DbContext EF habituel (mêmes entités, mêmes
/// comportements de suppression que le reste de l'application).
/// </summary>
public sealed class SiteDataResetService : ISiteDataResetService
{
    private readonly NovAccesIdentityDbContext _identityDb;
    private readonly ITenantProvisioningService _provisioning;
    private readonly ISiteCatalog _sites;
    private readonly string _ownerConnectionString;
    private readonly ILogger<SiteDataResetService> _logger;

    public SiteDataResetService(
        NovAccesIdentityDbContext identityDb,
        ITenantProvisioningService provisioning,
        ISiteCatalog sites,
        IConfiguration configuration,
        ILogger<SiteDataResetService> logger)
    {
        _identityDb = identityDb;
        _provisioning = provisioning;
        _sites = sites;
        _ownerConnectionString = PostgresAdminConnectionResolver.Resolve(configuration);
        _logger = logger;
    }

    public async Task ResetSiteAsync(string siteId, CancellationToken ct)
    {
        if (!CurrentTenant.IsValidSiteId(siteId))
            throw new ArgumentException($"Identifiant de site invalide : « {siteId} ».", nameof(siteId));

        // GetSiteIdsAsync (non caché), pas ExistsAsync (caché ~30s pour le
        // chemin des requêtes HTTP courantes) : un site qui vient tout juste
        // d'être (re)provisionné doit être vu tout de suite, pas dans 30s —
        // découvert par le test d'intégration, qui provisionne puis
        // réinitialise un site jetable en quelques millisecondes.
        var provisionedSites = await _sites.GetSiteIdsAsync(ct);
        if (!provisionedSites.Contains(siteId, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Site « {siteId} » non provisionné — rien à réinitialiser.");

        _logger.LogWarning("Réinitialisation COMPLÈTE du site « {SiteId} » démarrée.", siteId);

        // 1. Schéma du site (visites, scans, exclusions, audit) : DROP puis
        //    re-provisionnement immédiat via le service existant — on ne
        //    duplique aucun DDL, on réutilise la même logique testée que
        //    "provision-site" (schéma, modèle, journal append-only, grants).
        var tenant = new CurrentTenant();
        tenant.Resolve(siteId);
        await using (var connection = new NpgsqlConnection(_ownerConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant.SchemaName}\" CASCADE";
            await drop.ExecuteNonQueryAsync(ct);
        }
        await _provisioning.ProvisionAsync(siteId, ct);
        _logger.LogWarning("Schéma « {Schema} » réinitialisé (vide, structure + triggers recréés).", tenant.SchemaName);

        // 2. Comptes Hôte/Sûreté rattachés à CE site (Admin/SuperAdmin ont
        //    SiteId = null, donc jamais concernés par ce filtre).
        var siteUsers = await _identityDb.Users.Where(u => u.SiteId == siteId).ToListAsync(ct);
        _identityDb.Users.RemoveRange(siteUsers);

        // 3. Terminaux : ceux dédiés à CE SEUL site sont supprimés (avec
        //    leurs tickets d'enrôlement, FK Restrict — doivent être retirés
        //    avant le terminal) ; ceux partagés avec un AUTRE site ne
        //    perdent que leur rattachement à celui-ci (SiteIds est un
        //    text[] Postgres : retiré via SQL pour rester fiable, une
        //    mutation en place de la liste .NET n'est pas garantie détectée
        //    par le suivi de changement EF sur ce type de colonne).
        var terminals = await _identityDb.Terminals.ToListAsync(ct);
        var dedicated = terminals.Where(t => t.SiteIds.Count == 1 && t.SiteIds[0] == siteId).ToList();
        var shared = terminals.Where(t => t.SiteIds.Count > 1 && t.SiteIds.Contains(siteId)).ToList();

        if (dedicated.Count > 0)
        {
            var dedicatedIds = dedicated.Select(t => t.Id).ToList();
            var tickets = await _identityDb.TerminalEnrollmentTickets
                .Where(k => k.TerminalId != null && dedicatedIds.Contains(k.TerminalId.Value)).ToListAsync(ct);
            _identityDb.TerminalEnrollmentTickets.RemoveRange(tickets);
            _identityDb.Terminals.RemoveRange(dedicated);
        }

        // 4. Sessions de rafraîchissement et registre d'agents de ce site.
        var sessions = await _identityDb.RefreshSessions.Where(r => r.SiteId == siteId).ToListAsync(ct);
        _identityDb.RefreshSessions.RemoveRange(sessions);

        var agentRegistry = await _identityDb.AgentRegistry.Where(a => a.SiteId == siteId).ToListAsync(ct);
        _identityDb.AgentRegistry.RemoveRange(agentRegistry);

        await _identityDb.SaveChangesAsync(ct);

        if (shared.Count > 0)
        {
            await using var connection = new NpgsqlConnection(_ownerConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE identity.terminals SET \"SiteIds\" = array_remove(\"SiteIds\", @siteId) WHERE @siteId = ANY(\"SiteIds\")";
            command.Parameters.AddWithValue("siteId", siteId);
            await command.ExecuteNonQueryAsync(ct);
        }

        _logger.LogWarning(
            "Réinitialisation du site « {SiteId} » terminée : {Users} compte(s), {Dedicated} terminal(aux) dédié(s) supprimé(s), {Shared} terminal(aux) partagé(s) détaché(s), {Sessions} session(s), {Agents} entrée(s) de registre agent.",
            siteId, siteUsers.Count, dedicated.Count, shared.Count, sessions.Count, agentRegistry.Count);
    }

    public async Task ResetEntireDatabaseAsync(CancellationToken ct)
    {
        _logger.LogWarning("Remise à zéro COMPLÈTE de la base démarrée.");

        // GetSiteIdsAsync (non caché) : voir le commentaire équivalent dans
        // ResetSiteAsync — même raisonnement, ici appliqué à tous les sites.
        var siteIds = await _sites.GetSiteIdsAsync(ct);
        foreach (var siteId in siteIds)
        {
            var tenant = new CurrentTenant();
            tenant.Resolve(siteId);
            await using var connection = new NpgsqlConnection(_ownerConnectionString);
            await connection.OpenAsync(ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{tenant.SchemaName}\" CASCADE";
            await drop.ExecuteNonQueryAsync(ct);
        }
        _logger.LogWarning("{Count} schéma(s) de site déprovisionné(s) (DROP, pas de re-provisionnement).", siteIds.Count);

        // Comptes SuperAdmin à conserver — recherchés par appartenance au
        // rôle plutôt que via UserManager (qui applique le filtre de requête
        // "actif seulement") : un SuperAdmin archivé doit lui aussi survivre,
        // conformément au périmètre confirmé par l'utilisateur.
        var superAdminRoleId = await _identityDb.Roles
            .Where(r => r.Name == NovAccesRoles.SuperAdmin)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct);
        var superAdminUserIds = superAdminRoleId == Guid.Empty
            ? new List<Guid>()
            : await _identityDb.UserRoles
                .Where(ur => ur.RoleId == superAdminRoleId)
                .Select(ur => ur.UserId)
                .ToListAsync(ct);

        var usersToDelete = await _identityDb.Users
            .IgnoreQueryFilters()
            .Where(u => !superAdminUserIds.Contains(u.Id))
            .ToListAsync(ct);
        _identityDb.Users.RemoveRange(usersToDelete);

        // Tickets avant terminaux (FK Restrict), tous sites confondus — plus
        // aucun site ne survit pour les rattacher.
        var tickets = await _identityDb.TerminalEnrollmentTickets.ToListAsync(ct);
        _identityDb.TerminalEnrollmentTickets.RemoveRange(tickets);
        var terminals = await _identityDb.Terminals.ToListAsync(ct);
        _identityDb.Terminals.RemoveRange(terminals);

        var sessions = await _identityDb.RefreshSessions.ToListAsync(ct);
        _identityDb.RefreshSessions.RemoveRange(sessions);

        var agentRegistry = await _identityDb.AgentRegistry.ToListAsync(ct);
        _identityDb.AgentRegistry.RemoveRange(agentRegistry);

        var sites = await _identityDb.Sites.ToListAsync(ct);
        _identityDb.Sites.RemoveRange(sites);

        await _identityDb.SaveChangesAsync(ct);

        // application_audit est un journal append-only (CLAUDE.md §7) : il
        // n'est JAMAIS purgé, y compris par une remise à zéro complète — la
        // remise à zéro elle-même doit rester traçable a posteriori.
        _logger.LogWarning(
            "Remise à zéro complète terminée : {Sites} site(s) déprovisionné(s), {UsersDeleted} compte(s) supprimé(s) ({SuperAdmins} SuperAdmin conservé(s)), {Terminals} terminal(aux), {Tickets} ticket(s), {Sessions} session(s), {Agents} entrée(s) de registre agent.",
            siteIds.Count, usersToDelete.Count, superAdminUserIds.Count, terminals.Count, tickets.Count, sessions.Count, agentRegistry.Count);
    }
}
