namespace NovAcces.Infrastructure.Auth;

/// <summary>
/// Constantes du schéma d'authentification par clé API des terminaux agents.
/// Les terminaux eux-mêmes sont désormais enrôlés en base (voir
/// NovAcces.Infrastructure.Identity.Terminal / TerminalDirectory), gérés
/// depuis la console Admin — plus de liste statique en configuration.
/// </summary>
public static class ApiKeyOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}
