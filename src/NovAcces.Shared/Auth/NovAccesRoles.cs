namespace NovAcces.Shared.Auth;

/// <summary>
/// Les cinq profils RBAC du système (section 8.5 du CDC, moindre privilège).
/// Utilisés comme noms de rôles Identity ET comme noms de policies ASP.NET Core.
/// </summary>
public static class NovAccesRoles
{
    /// <summary>Hôte : crée des demandes de visite, révoque ses propres QR.</summary>
    public const string Hote = "Hote";

    /// <summary>Agent de contrôle : scanne les QR aux postes (via l'app React Native).</summary>
    public const string Agent = "Agent";

    /// <summary>Sûreté : dashboard, journal, révocation, supervision du site.</summary>
    public const string Surete = "Surete";

    /// <summary>Administrateur Sigasécurité : global, multi-sites, provisionnement.</summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Rôle du prestataire, au-dessus d'Admin. Un compte SuperAdmin reçoit
    /// AUSSI le rôle Admin (hérite donc de toutes ses capacités) mais est seul
    /// habilité à créer ou promouvoir un compte Admin/SuperAdmin — un Admin
    /// Sigasécurité ne peut jamais s'auto-accorder ni accorder à un tiers ce
    /// niveau global (protection contre l'auto-escalade, voir AuthEndpoints).
    /// </summary>
    public const string SuperAdmin = "SuperAdmin";

    public static readonly string[] All = { Hote, Agent, Surete, Admin, SuperAdmin };
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

    /// <summary>
    /// Un des sites qu'un terminal partagé est autorisé à servir (un claim par
    /// site, terminal multi-sites uniquement). Absent si le terminal ne sert
    /// qu'un seul site — dans ce cas <see cref="SiteId"/> suffit et fait foi.
    /// L'agent choisit le site parmi cette liste à la prise de poste ; le
    /// choix est revalidé côté serveur contre CETTE liste (jamais un site
    /// arbitraire), via l'en-tête X-Site-Id (TenantResolutionMiddleware).
    /// </summary>
    public const string AllowedSite = "novacces:allowed_site";

    /// <summary>Identifiant du terminal qui a authentifié une requête API key.</summary>
    public const string TerminalId = "novacces:terminal_id";
}

/// <summary>
/// Noms centralisés des policies d'autorisation transversales.
/// Les endpoints et hubs ne doivent pas recopier des chaînes de policy.
/// </summary>
public static class NovAccesPolicies
{
    public const string RevokeVisit = "RevokeVisit";
    public const string Dashboard = "Dashboard";
    public const string DashboardApi = "DashboardApi";

    /// <summary>
    /// Lecture du journal des scans du site et de ses agrégats (dashboard
    /// sûreté, export CSV). RÉSERVÉE à la Sûreté et l'Admin : le journal
    /// contient les allées et venues de TOUS les visiteurs du site, y compris
    /// ceux invités par d'autres hôtes. Un hôte n'a à connaître que ses propres
    /// demandes (moindre privilège, §8.5) — il consulte celles-ci via
    /// /api/visits/mine et /api/visits/{id}/history, qui filtrent sur lui.
    /// </summary>
    public const string SecurityJournal = "SecurityJournal";
    public const string AgentEvents = "AgentEvents";
    public const string ManageExclusions = "ManageExclusions";
    public const string AgentTerminal = "AgentTerminal";
}