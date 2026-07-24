using Xunit;

namespace NovAcces.IntegrationTests;

/// <summary>
/// Regroupe les tests d'intégration qui partagent une même base PostgreSQL et
/// les mêmes schémas de tenants. xUnit exécute en parallèle les classes de test
/// par défaut ; sans ce regroupement, deux classes provisionneraient les mêmes
/// schémas au même instant (collision). La collection garantit UNE seule
/// fixture partagée, provisionnée une fois, et sérialise ces classes entre elles.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresTenantFixture>
{
    public const string Name = "Postgres (intégration)";
}
