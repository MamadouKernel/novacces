using Microsoft.AspNetCore.SignalR;

namespace NovAcces.Api.Hubs;

/// <summary>
/// Canal de diffusion temps réel des scans (REQ-F-06, section 9 de
/// scenarios-fonctionnels.md — dashboard sûreté / portail hôte).
///
/// Le tenant résolu par TenantResolutionMiddleware (scoped à la requête
/// HTTP initiale de négociation) ne survit pas à la durée de vie de la
/// connexion WebSocket : chaque client indique donc explicitement son site
/// via le paramètre de requête "site" à la connexion, revalidé ici avec la
/// même règle que CurrentTenant.Resolve (whitelist alphanumérique) — jamais
/// fait confiance à une valeur non revalidée, même si le middleware a déjà
/// vérifié une requête HTTP précédente.
/// </summary>
public sealed class ScanEventsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var siteId = Context.GetHttpContext()?.Request.Query["site"].ToString();

        if (string.IsNullOrWhiteSpace(siteId) || !siteId.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, siteId.ToLowerInvariant());
        await base.OnConnectedAsync();
    }
}
