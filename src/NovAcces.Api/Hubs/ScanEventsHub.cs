using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Hubs;

/// <summary>
/// Canal de diffusion temps réel des scans (REQ-F-06, section 9 de
/// scenarios-fonctionnels.md — dashboard sûreté / portail hôte).
///
/// Le tenant résolu par TenantResolutionMiddleware (scoped à la requête
/// HTTP initiale de négociation) ne survit pas à la durée de vie de la
/// connexion WebSocket : chaque client indique donc explicitement son site
/// via le paramètre de requête "site" à la connexion, revalidé ici avec la
/// même règle que CurrentTenant.Resolve (CurrentTenant.IsValidSiteId).
///
/// CLOISONNEMENT (CLAUDE.md §7.3) : on applique ici la MÊME règle que le
/// middleware de tenant. Un utilisateur rattaché à un site (Hôte / Sûreté /
/// Agent) ne peut s'abonner qu'au flux de SON site — un paramètre "site"
/// visant un autre site est refusé (sinon fuite temps réel inter-clients).
/// Seul l'Admin global (sans claim de site) peut cibler un site précis.
/// </summary>
public sealed class ScanEventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var requested = http?.Request.Query["site"].ToString();
        if (string.IsNullOrWhiteSpace(requested))
            requested = http?.Request.Headers["X-Site-Id"].ToString();

        if (!CurrentTenant.IsValidSiteId(requested))
        {
            Context.Abort();
            return;
        }

        // Le site du compte fait foi : un utilisateur rattaché à un site ne peut
        // rejoindre que le groupe de CE site. Admin global (pas de claim) : libre.
        var claimSite = Context.User?.FindFirstValue(NovAccesClaimTypes.SiteId);
        if (!string.IsNullOrWhiteSpace(claimSite)
            && !string.Equals(claimSite, requested, StringComparison.OrdinalIgnoreCase))
        {
            Context.Abort();
            return;
        }

        var allowedSites = Context.User?.FindAll(NovAccesClaimTypes.AllowedSite)
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        // Un terminal multi-sites ne possède volontairement pas de claim SiteId :
        // son abonnement doit toutefois rester borné à AllowedSite.
        if (allowedSites.Count > 0
            && !allowedSites.Contains(requested!, StringComparer.OrdinalIgnoreCase))
        {
            Context.Abort();
            return;
        }

        // Une identité sans site ni liste autorisée est globale uniquement pour
        // Admin/SuperAdmin. Toute autre identité est refusée par défaut.
        if (string.IsNullOrWhiteSpace(claimSite)
            && allowedSites.Count == 0
            && !NovAccesAuthorizationMatrix.IsGlobalOperator(
                Context.User ?? new ClaimsPrincipal()))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, requested!.ToLowerInvariant());
        await base.OnConnectedAsync();
    }
}
