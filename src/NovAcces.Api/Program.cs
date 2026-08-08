using NovAcces.Shared.Auth;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
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

// Clés DataProtection (jetons Identity : réinitialisation de mot de passe,
// confirmation d'email) persistées sur un volume Docker dédié (voir
// docker-compose.yml, service "api") — sans quoi chaque recréation du
// conteneur au redéploiement invalide silencieusement tout jeton en cours.
// En développement, le répertoire par défaut d'ASP.NET Core suffit.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo("/app/keys"))
        .SetApplicationName("SigasAcces.Api");
}

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
builder.Services.AddSwaggerGen(options =>
{
    // Retour du dev app agent (05/08/2026) : components.securitySchemes vide,
    // 0 en-tête déclaré sur 80 opérations, alors que tous les endpoints agent
    // répondent 401 WWW-Authenticate: Bearer. Voir AgentSecurityRequirementsOperationFilter.
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Jeton JWT — login web/agent (POST /api/auth/login) ou jeton de poste "
            + "(POST /api/agent/shift/start). Format : \"Bearer {token}\".",
    });
    options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = NovAcces.Infrastructure.Auth.ApiKeyOptions.HeaderName,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Clé API du terminal, remise UNE SEULE FOIS en réponse de "
            + "POST /api/device-enrollments/activate. Exigée sur CHAQUE requête "
            + "/api/agent/* et du contrat React Native — même en présence d'un Bearer.",
    });
    options.OperationFilter<NovAcces.Api.Configuration.AgentSecurityRequirementsOperationFilter>();
});

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

// --- Commande d'administration hors-ligne : génération des clés VAPID ---
//   dotnet NovAcces.Api.dll generate-vapid-keys
// À exécuter UNE SEULE FOIS, avant la toute première mise en service des
// notifications push navigateur (§7 — alerte de dépassement même onglet
// fermé). Ne JAMAIS régénérer après coup : ça invaliderait tous les
// abonnements déjà enregistrés par les navigateurs (chacun devrait se
// réabonner). Coller les valeurs affichées dans WEBPUSH_VAPID_PUBLIC_KEY /
// WEBPUSH_VAPID_PRIVATE_KEY de .env (voir .env.example).
if (args.Length >= 1 && args[0] == "generate-vapid-keys")
{
    var keys = WebPush.VapidHelper.GenerateVapidKeys();
    Console.WriteLine("WEBPUSH_VAPID_PUBLIC_KEY=" + keys.PublicKey);
    Console.WriteLine("WEBPUSH_VAPID_PRIVATE_KEY=" + keys.PrivateKey);
    return 0;
}

// --- Commande d'administration hors-ligne : restauration d'une sauvegarde ---
//   dotnet NovAcces.Api.dll restore-database <fichier.dump> --confirm <fichier.dump>
// ÉCRASE la base COMPLÈTE (tous les sites) avec le contenu de la sauvegarde.
// Volontairement CLI uniquement, jamais un endpoint HTTP (voir
// IDatabaseBackupService) : un geste d'exploitation délibéré exécuté par un
// opérateur qui a accès au serveur, pas un bouton cliquable pendant que
// l'API sert du trafic live. --confirm doit répéter EXACTEMENT le nom du
// fichier — une faute de frappe volontaire est le seul filet de sécurité
// contre un copier-coller de commande sur la mauvaise ligne.
if (args.Length >= 1 && args[0] == "restore-database")
{
    if (args.Length < 4 || args[2] != "--confirm" || args[1] != args[3])
    {
        Console.Error.WriteLine("Usage : dotnet NovAcces.Api.dll restore-database <fichier.dump> --confirm <fichier.dump>");
        Console.Error.WriteLine("Le nom du fichier doit être répété IDENTIQUE après --confirm.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("ATTENTION : cette commande ÉCRASE la base complète (tous les sites) avec");
        Console.Error.WriteLine("le contenu de la sauvegarde. Arrêtez le trafic (docker compose stop api web)");
        Console.Error.WriteLine("avant de l'exécuter — restaurer pendant que l'API sert des requêtes live");
        Console.Error.WriteLine("peut laisser la base dans un état incohérent.");
        return 1;
    }

    using var scope = app.Services.CreateScope();
    var backups = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();

    Console.WriteLine($"Restauration depuis « {args[1]} »...");
    try
    {
        await backups.RestoreBackupAsync(args[1], default);
    }
    catch (Exception ex) when (ex is DatabaseBackupFailedException or DatabaseBackupInProgressException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    Console.WriteLine("Restauration terminée. Relancez les conteneurs api/web si vous les aviez arrêtés.");
    return 0;
}

// --- Commande d'administration hors-ligne : réinitialisation d'un site ---
//   dotnet NovAcces.Api.dll reset-site-data <siteId> --confirm <siteId>
// Vide COMPLÈTEMENT les données d'UN site (visites/scans/exclusions/audit,
// comptes Hôte/Sûreté rattachés, terminaux dédiés, sessions, registre
// d'agents) pour repartir de zéro en phase de test/pilote — voir
// ISiteDataResetService pour le détail exact du périmètre. Volontairement
// CLI uniquement, jamais un endpoint HTTP : irréversible, un site à la fois,
// --confirm doit répéter EXACTEMENT le même identifiant de site.
if (args.Length >= 1 && args[0] == "reset-site-data")
{
    if (args.Length < 4 || args[2] != "--confirm" || args[1] != args[3])
    {
        Console.Error.WriteLine("Usage : dotnet NovAcces.Api.dll reset-site-data <siteId> --confirm <siteId>");
        Console.Error.WriteLine("Le siteId doit être répété IDENTIQUE après --confirm.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("ATTENTION : vide COMPLÈTEMENT les données de ce site (visites, scans,");
        Console.Error.WriteLine("exclusions, audit, comptes Hôte/Sûreté rattachés, terminaux dédiés,");
        Console.Error.WriteLine("sessions, registre d'agents). Irréversible — pensez à une sauvegarde");
        Console.Error.WriteLine("préalable (voir la console SuperAdmin) si un doute subsiste.");
        return 1;
    }

    using var scope = app.Services.CreateScope();
    var reset = scope.ServiceProvider.GetRequiredService<ISiteDataResetService>();

    Console.WriteLine($"Réinitialisation du site « {args[1]} »...");
    try
    {
        await reset.ResetSiteAsync(args[1], default);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    Console.WriteLine("Site réinitialisé : schéma vide recréé, comptes/terminaux dédiés supprimés.");
    return 0;
}

// --- Commande d'administration hors-ligne : remise à zéro COMPLÈTE ---
//   dotnet NovAcces.Api.dll reset-database --confirm SUPPRIMER-TOUT-SAUF-SUPERADMIN
// Déprovisionne TOUS les sites (DROP des schémas, pas de re-provisionnement)
// et supprime TOUS les comptes sauf ceux portant le rôle SuperAdmin — voir
// ISiteDataResetService.ResetEntireDatabaseAsync pour le détail exact du
// périmètre. L'action la plus destructrice de toute l'application :
// volontairement CLI uniquement, jamais un endpoint HTTP, confirmée par une
// phrase fixe (pas un identifiant à retrouver) pour qu'un copier-coller
// négligent ne puisse jamais la déclencher par accident.
if (args.Length >= 1 && args[0] == "reset-database")
{
    const string confirmPhrase = "SUPPRIMER-TOUT-SAUF-SUPERADMIN";
    if (args.Length < 3 || args[1] != "--confirm" || args[2] != confirmPhrase)
    {
        Console.Error.WriteLine($"Usage : dotnet NovAcces.Api.dll reset-database --confirm {confirmPhrase}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("ATTENTION : remise à zéro COMPLÈTE de la base. Déprovisionne TOUS les");
        Console.Error.WriteLine("sites (visites, scans, exclusions, audit — schémas supprimés, pas");
        Console.Error.WriteLine("recréés) et supprime TOUS les comptes SAUF les SuperAdmin. Terminaux,");
        Console.Error.WriteLine("tickets, sessions et registre d'agents entièrement vidés. Irréversible —");
        Console.Error.WriteLine("pensez à une sauvegarde préalable (voir la console SuperAdmin).");
        return 1;
    }

    using var scope = app.Services.CreateScope();
    var reset = scope.ServiceProvider.GetRequiredService<ISiteDataResetService>();

    Console.WriteLine("Remise à zéro complète de la base en cours...");
    await reset.ResetEntireDatabaseAsync(default);

    Console.WriteLine("Base réinitialisée : tous les sites déprovisionnés, seuls les comptes SuperAdmin subsistent.");
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
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
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

// Sonde de santé : vérifie que le schéma identity partagé est réellement
// accessible ET à jour, pas seulement que le processus répond. C'est cette
// requête qui aurait détecté en quelques secondes l'incident du 03/08/2026
// (table identity.sites absente faute de migration rejouée après déploiement)
// au lieu de le laisser découvrir manuellement des jours plus tard. Le détail
// de l'exception va aux logs serveur, jamais à la réponse (non authentifiée).
app.MapGet("/health", async (NovAccesIdentityDbContext identityDb, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        await identityDb.Sites.AsNoTracking().Select(s => s.SiteId).Take(1).ToListAsync(ct);
        return Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Sonde de santé en échec : schéma identity inaccessible ou migrations non à jour.");
        return Results.Json(new { status = "degraded" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapAuthEndpoints().RequireRateLimiting("auth");
app.MapScanEndpoints().RequireRateLimiting("sensitive");
app.MapVisitEndpoints().RequireRateLimiting("sensitive");
app.MapDashboardEndpoints();
app.MapExclusionEndpoints();
app.MapAuditEndpoints();
app.MapAgentEndpoints().RequireRateLimiting("sensitive");
app.MapDeviceEnrollmentEndpoints();
app.MapAdminEndpoints();
app.MapDatabaseAdminEndpoints().RequireRateLimiting("sensitive");
app.MapPushEndpoints();
app.MapHub<ScanEventsHub>("/hubs/scan").RequireAuthorization(NovAccesPolicies.Dashboard);
app.MapHub<ScanEventsHub>("/hubs/scan-events").RequireAuthorization(NovAccesPolicies.AgentEvents);
app.MapAgentContractEndpoints();

app.Run();

return 0;

// Rendu accessible aux tests d'intégration (WebApplicationFactory<Program>).
public partial class Program;
