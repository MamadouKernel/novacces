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
        var requested = Context.GetHttpContext()?.Request.Query["site"].ToString();

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

        await Groups.AddToGroupAsync(Context.ConnectionId, requested!.ToLowerInvariant());
        await base.OnConnectedAsync();
    }
}
