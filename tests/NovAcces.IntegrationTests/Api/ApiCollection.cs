using Xunit;

namespace NovAcces.IntegrationTests.Api;

/// <summary>
/// Regroupe les tests d'intégration qui démarrent l'API en mémoire. Ils
/// partagent UNE seule NovAccesApiFactory (donc un seul amorçage / provisionnement)
/// et sont sérialisés entre eux, évitant que deux factories provisionnent le même
/// site en parallèle (collision DDL).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<NovAccesApiFactory>
{
    public const string Name = "API (intégration)";
}
