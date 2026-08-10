using Microsoft.Extensions.Configuration;
using Npgsql;

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
        if (IsUsable(owner))
            return owner!;

        return configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.");
    }

    /// <summary>
    /// Une chaîne "PostgresOwner" n'est considérée configurée que si elle
    /// porte un nom d'utilisateur non vide — docker-compose.yml émet toujours
    /// la variable (gabarit avec Username=${POSTGRES_OWNER_USER:-}), qui vaut
    /// une chaîne non vide même quand .env ne renseigne pas
    /// POSTGRES_OWNER_USER/PASSWORD. Un simple IsNullOrWhiteSpace sur la
    /// chaîne brute la traiterait à tort comme "configurée".
    /// </summary>
    public static bool IsUsable(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            return !string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(connectionString).Username);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
