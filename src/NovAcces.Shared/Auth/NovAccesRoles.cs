namespace NovAcces.Shared.Auth;

/// <summary>
/// Les quatre profils RBAC du système (section 8.5 du CDC, moindre privilège).
/// Utilisés comme noms de rôles Identity ET comme noms de policies ASP.NET Core.
/// </summary>
public static class NovAccesRoles
{
    /// <summary>Hôte : crée des demandes de visite, révoque ses propres QR.</summary>
    public const string Hote = "Hote";

    /// <summary>Agent de contrôle : scanne les QR aux postes (via app MAUI).</summary>
    public const string Agent = "Agent";

    /// <summary>Sûreté : dashboard, journal, révocation, supervision du site.</summary>
    public const string Surete = "Surete";

    /// <summary>Administrateur Sigasécurité : global, multi-sites, provisionnement.</summary>
    public const string Admin = "Admin";

    public static readonly string[] All = { Hote, Agent, Surete, Admin };
}

/// <summary>
/// Types de claims spécifiques à NovAcces, portés par le principal authentifié.
/// </summary>
public static class NovAccesClaimTypes
{
    /// <summary>
    /// Site de rattachement de l'utilisateur (ex. "sicopa"). Absent pour un
    /// Admin global. C'est CE claim — et non un en-tête client falsifiable —
    /// qui détermine le tenant d'une requête authentifiée (cloisonnement §7.3).
    /// </summary>
    public const string SiteId = "novacces:site_id";
}
