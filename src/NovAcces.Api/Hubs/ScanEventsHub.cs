using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using NovAcces.Api.Endpoints;
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
    /// <summary>
    /// Groupe « tous sites » : diffuse une version allégée (sans nom de
    /// visiteur) de chaque scan, pour le dashboard Admin multi-sites. Réservé
    /// aux Admin/SuperAdmin — aucune notion de site n'a de sens ici.
    /// </summary>
    public const string GlobalGroup = "__all_sites__";

    private static readonly ConcurrentDictionary<string, int> ConnectionsPerIp = new();
    private const int MaxConnectionsPerIp = 25;

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var clientIp = http?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var count = ConnectionsPerIp.AddOrUpdate(clientIp, 1, (_, current) => current + 1);
        if (count > MaxConnectionsPerIp)
        {
            DecrementConnectionCount(clientIp);
            Context.Abort();
            return;
        }

        if (http?.Request.Query["global"].ToString() == "1")
        {
            if (!NovAccesAuthorizationMatrix.IsGlobalOperator(Context.User ?? new ClaimsPrincipal()))
            {
                DecrementConnectionCount(clientIp);
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GlobalGroup);
            await base.OnConnectedAsync();
            return;
        }

        // Portail Hôte : groupe personnel (host:{HostUserId}), JAMAIS le groupe
        // du site — un hôte n'a pas à voir le flux des autres visiteurs du site
        // (moindre privilège, §8.5). L'identifiant vient du jeton authentifié
        // (même claim que ClaimsPrincipalExtensions.HostIdentifier côté API),
        // jamais d'un paramètre fourni par le client : usurper le groupe d'un
        // autre hôte reviendrait à intercepter ses alertes de dépassement.
        if (http?.Request.Query["hote"].ToString() == "1")
        {
            var hostId = Context.User?.HostIdentifier();
            if (string.IsNullOrWhiteSpace(hostId) || hostId == "inconnu")
            {
                DecrementConnectionCount(clientIp);
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, ScanEventBroadcaster.HostGroup(hostId));
            await base.OnConnectedAsync();
            return;
        }

        var requested = http?.Request.Query["site"].ToString();
        if (string.IsNullOrWhiteSpace(requested))
            requested = http?.Request.Headers["X-Site-Id"].ToString();

        if (!CurrentTenant.IsValidSiteId(requested))
        {
            DecrementConnectionCount(clientIp);
            Context.Abort();
            return;
        }

        // Le site du compte fait foi : un utilisateur rattaché à un site ne peut
        // rejoindre que le groupe de CE site. Admin global (pas de claim) : libre.
        var claimSite = Context.User?.FindFirstValue(NovAccesClaimTypes.SiteId);
        if (!string.IsNullOrWhiteSpace(claimSite)
            && !string.Equals(claimSite, requested, StringComparison.OrdinalIgnoreCase))
        {
            DecrementConnectionCount(clientIp);
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
            DecrementConnectionCount(clientIp);
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
            DecrementConnectionCount(clientIp);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, requested!.ToLowerInvariant());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var http = Context.GetHttpContext();
        var clientIp = http?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        DecrementConnectionCount(clientIp);
        await base.OnDisconnectedAsync(exception);
    }

    private static void DecrementConnectionCount(string clientIp)
    {
        ConnectionsPerIp.AddOrUpdate(clientIp, 0, (_, current) => Math.Max(0, current - 1));
    }
}
