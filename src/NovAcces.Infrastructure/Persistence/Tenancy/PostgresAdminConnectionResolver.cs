using Microsoft.Extensions.Configuration;

namespace NovAcces.Infrastructure.Persistence.Tenancy;

/// <summary>
/// Résout LA chaîne de connexion à utiliser pour une opération d'administration
/// transverse (DDL, sauvegarde/restauration, diagnostic base) : la connexion
/// PROPRIÉTAIRE si elle est configurée, sinon celle du runtime — même repli
/// que <see cref="TenantProvisioningService"/>, centralisé ici pour ne pas le
/// répéter dans chaque service d'administration (backup, santé, requêtes SQL).
/// </summary>
public static class PostgresAdminConnectionResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var owner = configuration.GetConnectionString("PostgresOwner");
        if (!string.IsNullOrWhiteSpace(owner))
            return owner;

        return configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.");
    }
}
