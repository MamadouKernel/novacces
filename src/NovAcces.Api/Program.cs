using NovAcces.Shared.Auth;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using NovAcces.Api;
using NovAcces.Api.Auth;
using NovAcces.Api.Configuration;
using NovAcces.Api.Endpoints;
using NovAcces.Api.Hubs;
using NovAcces.Api.Middleware;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence.Tenancy;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
    ProductionConfigurationValidator.Validate(builder.Configuration);

// --- Services ---
builder.Services.AddNovAccesInfrastructure(builder.Configuration);

// Identité (schéma partagé), JWT, clés API terminaux, puis schémas d'auth + RBAC.
// AddDefaultTokenProviders (2FA TOTP + codes de récupération) est ajouté ici,
// côté hôte web, car il dépend de l'assembly ASP.NET Core Identity.
builder.Services.AddNovAccesIdentity(builder.Configuration).AddDefaultTokenProviders();
builder.Services.Configure<AuthenticationSecurityOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddNovAccesAuthentication(builder.Configuration);
builder.Services.AddNovAccesAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- En-têtes de proxy inverse (nginx/Caddy devant l'API sur le VPS) ---
// SANS cette configuration, RemoteIpAddress vaut l'adresse du proxy pour TOUTES
// les requêtes. Deux conséquences graves en production :
//   1. les partitions du rate limiting s'effondrent en un seul seau : la limite
//      « 30 scans/min » s'appliquerait à l'ENSEMBLE du parc de terminaux, et
//      « 10 connexions/min » à tous les utilisateurs réunis — déni de service
//      auto-infligé, et fin de la protection anti-brute-force par IP ;
//   2. le journal global (ApplicationAuditMiddleware) enregistrerait l'IP du
//      proxy sur chaque ligne, ce qui lui retire toute valeur d'enquête.
//
// On n'accepte l'en-tête QUE de proxys explicitement déclarés : X-Forwarded-For
// est trivialement falsifiable par le client, le faire confiance sans liste
// blanche permettrait à n'importe qui de se forger une IP et de contourner le
// rate limiting comme la traçabilité.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Par défaut ASP.NET Core fait confiance à la loopback ; on repart d'une
    // liste vide pour la reconstituer explicitement depuis la configuration.
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();

    var configuredProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>()
        ?? Array.Empty<string>();
    foreach (var proxy in configuredProxies)
    {
        if (IPAddress.TryParse(proxy, out var address))
            options.KnownProxies.Add(address);
        else
            throw new InvalidOperationException(
                $"ForwardedHeaders:KnownProxies contient une adresse IP invalide : « {proxy} ».");
    }

    // Déploiement usuel : proxy sur la même machine que l'API (Contabo).
    if (options.KnownProxies.Count == 0)
    {
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }

    // Nombre de sauts de proxy à dépouiller. Un seul par défaut : au-delà, un
    // client pourrait pré-remplir X-Forwarded-For et faire remonter une IP de
    // son choix jusqu'à RemoteIpAddress.
    options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
});

// Diffusion temps réel des scans (REQ-F-06) — le Hub dépend d'ASP.NET Core,
// il vit donc dans Api et non dans Infrastructure (Clean Architecture).
builder.Services.AddSignalR();
builder.Services.AddScoped<IScanEventBroadcaster, ScanEventBroadcaster>();
builder.Services.AddScoped<IAgentEventBroadcaster, ScanEventBroadcaster>();
builder.Services.AddScoped<IAdminActivityBroadcaster, ScanEventBroadcaster>();

// Supervision des dépassements de durée (§7) : service de fond périodique.
builder.Services.AddHostedService<OverstayMonitor>();

// Purge des données au-delà de la durée de conservation (§7.3) : service de fond.
builder.Services.AddHostedService<RetentionMonitor>();

// Rate limiting natif .NET 8 sur les endpoints sensibles (section 8.2 du CDC).
// Politique nommée appliquée explicitement sur /api/scan et /api/visits ci-dessous.
var rateLimitPermit = builder.Configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 30;
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("sensitive", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            "sensitive:"
                + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
                + ":"
                + (httpContext.User.FindFirst("novacces:terminal_id")?.Value ?? "anonymous"),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitPermit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Politique dédiée à l'authentification, partitionnée PAR IP : limite le
    // brute-force / password-spraying sur /api/auth (login, 2FA) — au-delà du
    // verrouillage de compte qui, lui, est par compte. 10 requêtes/min/IP.
    var authPermit = builder.Configuration.GetValue<int?>("RateLimiting:AuthPermitLimit") ?? 10;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

// --- Commande d'administration hors-ligne : provisionnement d'un site ---
//   dotnet run -- provision-site <siteId>
// Crée le schéma PostgreSQL du site, applique le modèle de données et rend le
// journal des scans append-only. Disponible aussi en CLI pour l'exploitation ;
// l'équivalent HTTP réservé à l'Admin existe (POST /api/admin/sites).
if (args.Length >= 1 && args[0] == "provision-site")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage : dotnet run -- provision-site <siteId>");
        return 1;
    }

    using var scope = app.Services.CreateScope();
    var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
    await provisioner.ProvisionAsync(args[1]);
    Console.WriteLine($"Site '{args[1]}' provisionné : schéma, modèle de données et journal append-only.");
    return 0;
}

// --- Commande d'administration hors-ligne : habilitation du rôle applicatif ---
//   dotnet run -- grant-app-role
// (Ré)applique les privilèges de Database:ApplicationRole sur le schéma partagé
// et sur tous les sites déjà provisionnés. À rejouer après une migration qui
// ajoute des tables, et lors du passage à deux rôles sur une base en service.
if (args.Length >= 1 && args[0] == "grant-app-role")
{
    using var scope = app.Services.CreateScope();
    var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
    if (provisioner is not TenantProvisioningService concrete)
    {
        Console.Error.WriteLine("Service de provisionnement indisponible.");
        return 1;
    }

    try
    {
        await concrete.ApplyApplicationRoleGrantsEverywhereAsync();
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine("Voir tools/provisionner-roles-postgres.sql pour créer le rôle applicatif.");
        return 1;
    }

    Console.WriteLine("Privilèges du rôle applicatif appliqués (schéma partagé + tous les sites).");
    return 0;
}

// --- Commande d'administration hors-ligne : migrations + amorçage initial ---
//   dotnet NovAcces.Api.dll migrate
// Applique les migrations EF (idempotentes) et amorce rôles + compte Admin
// initial (SeedAdmin:Email/Password) si absent. Automatique en développement
// (voir plus bas) ; en production c'est un geste d'exploitation EXPLICITE,
// à rejouer après chaque déploiement qui ajoute une migration, et lors de la
// toute première mise en service pour créer le compte Admin initial.
if (args.Length >= 1 && args[0] == "migrate")
{
    await app.EnsureIdentityReadyAsync();
    Console.WriteLine("Migrations appliquées, rôles et compte Admin initial vérifiés.");
    return 0;
}

// Swagger : activé en dev par défaut, ou explicitement en production via
// Api:EnableSwagger (TEMPORAIRE, 02/08/2026, Mamadou — à repasser à false une
// fois la doc consultée, ne doit pas rester ouvert en continu sur le VPS).
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:EnableSwagger"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = app.Environment.IsDevelopment() ? string.Empty : "swagger";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "NovAcces.Api v1");
    });
}

if (app.Environment.IsDevelopment())
{
    // Applique le schéma Identity (schéma partagé) et amorce rôles + Admin de
    // développement, pour que la connexion soit testable immédiatement. En
    // production, migration et création du premier Admin sont des gestes
    // d'exploitation explicites, pas un effet de bord du démarrage.
    await app.EnsureIdentityReadyAsync();
}

// DOIT être le tout premier middleware : le rate limiting, le journal global et
// la redirection HTTPS lisent tous l'IP et le schéma de la requête. S'il était
// placé plus bas, ils travailleraient sur l'adresse du proxy.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler();

// En-têtes de sécurité HTTP (OWASP A05) sur TOUTES les réponses : anti-sniffing
// MIME, anti-clickjacking, pas de fuite de referrer, pas de politique Flash/PDF.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Permitted-Cross-Domain-Policies"] = "none";
    await next();
});

// HSTS en production (force HTTPS côté navigateur/clients). Pas en dev/test.
if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Désactivable en test d'intégration (TestServer tourne en HTTP, la redirection
// produirait des 307 parasites). Toujours actif en dev/prod.
if (!app.Configuration.GetValue<bool>("DisableHttpsRedirection"))
    app.UseHttpsRedirection();

// L'authentification DOIT précéder la résolution de tenant : celle-ci lit le
// claim SiteId du principal authentifié.
// Le journal global enveloppe tout le pipeline : les refus d'authentification,
// de tenant et de rate limiting sont eux aussi traçables.
app.UseApplicationAudit();
app.UseAuthentication();
app.UseTenantResolution();

// Rate limiting désactivable en test d'intégration (la suite sérialisée dépasse
// sinon la politique « sensitive »). Toujours actif en dev/prod.
if (!app.Configuration.GetValue<bool>("RateLimiting:Disabled"))
    app.UseRateLimiter();

app.UseAuthorization();

app.UseActiveUser();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow, serverTimeUtc = DateTimeOffset.UtcNow }));

app.MapAuthEndpoints().RequireRateLimiting("auth");
app.MapScanEndpoints().RequireRateLimiting("sensitive");
app.MapVisitEndpoints().RequireRateLimiting("sensitive");
app.MapDashboardEndpoints();
app.MapExclusionEndpoints();
app.MapAuditEndpoints();
app.MapAgentEndpoints();
app.MapDeviceEnrollmentEndpoints();
app.MapAdminEndpoints();
app.MapHub<ScanEventsHub>("/hubs/scan").RequireAuthorization(NovAccesPolicies.Dashboard);
app.MapHub<ScanEventsHub>("/hubs/scan-events").RequireAuthorization(NovAccesPolicies.AgentEvents);
app.MapAgentContractEndpoints();

app.Run();

return 0;

// Rendu accessible aux tests d'intégration (WebApplicationFactory<Program>).
public partial class Program;
