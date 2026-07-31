using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Provisionne le schéma PostgreSQL d'un site et le rend prêt à l'emploi.
/// Idempotent : ré-exécutable sans effet de bord.
///
/// ZONES SENSIBLES — voir CLAUDE.md §7.3 (cloisonnement) et §7.4 (journal
/// INSERT-only). Toute modification doit être relue humainement et couverte
/// par un test d'intégration (cf. TenantProvisioningTests).
///
/// Note : ce service travaille sur des connexions BRUTES (NpgsqlConnection),
/// délibérément SANS l'intercepteur de tenant. L'intercepteur positionne le
/// search_path sur le schéma du tenant à l'ouverture — or ce schéma n'existe
/// pas encore ici : c'est précisément ce qu'on est en train de créer.
/// </summary>
public sealed class TenantProvisioningService : ITenantProvisioningService
{
    private readonly string _connectionString;

    public TenantProvisioningService(string connectionString) =>
        _connectionString = connectionString;

    public async Task ProvisionAsync(string siteId, CancellationToken ct = default)
    {
        // Réutilise la validation stricte du reste du système (whitelist ASCII,
        // longueur bornée) — la seule source autorisée de noms de schéma.
        if (!CurrentTenant.IsValidSiteId(siteId))
            throw new ArgumentException($"Identifiant de site invalide : '{siteId}'.", nameof(siteId));

        var tenant = new CurrentTenant();
        tenant.Resolve(siteId);
        var schema = tenant.SchemaName; // "site_<siteId>", déjà assaini

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 1. Schéma dédié au site.
        await ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", ct);

        // 2. Application du modèle de données EF Core DANS ce schéma.
        //    Le search_path est positionné sur la connexion courante, puis le
        //    script (idempotent) crée les tables sans schéma qualifié — elles
        //    atterrissent donc dans le schéma du site.
        await ExecuteAsync(connection, $"SET search_path TO \"{schema}\"", ct);
        await ExecuteAsync(connection, BuildModelScript(tenant), ct);

        // 3. Journaux inaltérables (CLAUDE.md §7.4, REQ-SEC-05, §8.5) :
        //    UPDATE/DELETE/TRUNCATE bloqués au niveau base, y compris pour un
        //    superutilisateur (les triggers s'exécutent quel que soit le rôle).
        //    Le search_path est toujours positionné, donc les objets visent bien
        //    le schéma. Deux journaux protégés : scan_logs (contrôle d'accès) et
        //    admin_audit (actions d'administration/sûreté).
        await ExecuteAsync(connection, AppendOnlyJournalDdl, ct);
    }

    /// <summary>
    /// Génère le DDL du modèle EF Core (variante idempotente). Hors connexion :
    /// GenerateScript ne touche pas la base, on peut donc utiliser un contexte
    /// non intercepté sans risque de search_path.
    /// </summary>
    private string BuildModelScript(CurrentTenant tenant)
    {
        var options = new DbContextOptionsBuilder<NovAccesDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        using var ctx = new NovAccesDbContext(options, tenant);
        var migrator = ctx.GetInfrastructure().GetRequiredService<IMigrator>();
        return migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    // Triggers append-only sur les journaux : lèvent une exception à toute
    // tentative de modification/suppression. Idempotent (CREATE OR REPLACE +
    // DROP IF EXISTS). Une seule fonction générique, réutilisée par les deux
    // journaux (scan_logs et admin_audit) — le message cite la table via
    // TG_TABLE_NAME pour rester exact quel que soit le déclencheur.
    //
    // SEULE EXCEPTION tolérée à l'immuabilité (RGPD/ARTCI, §7.3) : dans
    // scan_logs uniquement, le nom du visiteur peut être remplacé par le
    // sentinel '[anonymisé]' après la fenêtre de conservation — en avant
    // seulement (OLD <> sentinel), et à la stricte condition qu'AUCUNE autre
    // colonne ne change (comparaison via jsonb, robuste à toute évolution de
    // schéma : une colonne ajoutée plus tard reste automatiquement protégée).
    // Aucun fait de sécurité (verdict, horodatage, agent, événement) ne peut
    // donc être altéré, et rien ne peut être supprimé. admin_audit ne comporte
    // pas de nom de visiteur (minimisation) : il reste totalement verrouillé.
    //
    // ATTENTION : le sentinel ci-dessous DOIT rester identique à
    // RetentionOptions.AnonymizationSentinel (côté application).
    private const string AppendOnlyJournalDdl = """
        CREATE OR REPLACE FUNCTION forbid_journal_mutation() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            -- Suppression / troncature : toujours interdites, sur tous les journaux.
            IF TG_OP <> 'UPDATE' THEN
                RAISE EXCEPTION '% est append-only : DELETE/TRUNCATE interdits (journal inaltérable, CDC §7.5/8.5).', TG_TABLE_NAME;
            END IF;

            -- Unique modification permise : l'anonymisation du nom dans scan_logs.
            IF TG_TABLE_NAME = 'scan_logs' THEN
                IF NEW."VisitorName" = '[anonymisé]'
                   AND OLD."VisitorName" <> '[anonymisé]'
                   AND (to_jsonb(OLD) - 'VisitorName') = (to_jsonb(NEW) - 'VisitorName')
                THEN
                    RETURN NEW;
                END IF;
            END IF;

            RAISE EXCEPTION '% est append-only : seule l''anonymisation du nom de visiteur (RGPD/ARTCI) est permise, aucune autre modification (journal inaltérable, CDC §7.5/8.5).', TG_TABLE_NAME;
        END;
        $$;

        DROP TRIGGER IF EXISTS scan_logs_append_only ON scan_logs;
        CREATE TRIGGER scan_logs_append_only
            BEFORE UPDATE OR DELETE ON scan_logs
            FOR EACH ROW EXECUTE FUNCTION forbid_journal_mutation();

        DROP TRIGGER IF EXISTS scan_logs_no_truncate ON scan_logs;
        CREATE TRIGGER scan_logs_no_truncate
            BEFORE TRUNCATE ON scan_logs
            FOR EACH STATEMENT EXECUTE FUNCTION forbid_journal_mutation();

        DROP TRIGGER IF EXISTS admin_audit_append_only ON admin_audit;
        CREATE TRIGGER admin_audit_append_only
            BEFORE UPDATE OR DELETE ON admin_audit
            FOR EACH ROW EXECUTE FUNCTION forbid_journal_mutation();

        DROP TRIGGER IF EXISTS admin_audit_no_truncate ON admin_audit;
        CREATE TRIGGER admin_audit_no_truncate
            BEFORE TRUNCATE ON admin_audit
            FOR EACH STATEMENT EXECUTE FUNCTION forbid_journal_mutation();
        """;
}
