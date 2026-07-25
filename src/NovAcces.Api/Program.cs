using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using NovAcces.Api;
using NovAcces.Api.Auth;
using NovAcces.Api.Endpoints;
using NovAcces.Api.Hubs;
using NovAcces.Api.Middleware;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure;
using NovAcces.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddNovAccesInfrastructure(builder.Configuration);

// Identité (schéma partagé), JWT, clés API terminaux, puis schémas d'auth + RBAC.
// AddDefaultTokenProviders (2FA TOTP + codes de récupération) est ajouté ici,
// côté hôte web, car il dépend de l'assembly ASP.NET Core Identity.
builder.Services.AddNovAccesIdentity(builder.Configuration).AddDefaultTokenProviders();
builder.Services.AddNovAccesAuthentication(builder.Configuration);
builder.Services.AddNovAccesAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Diffusion temps réel des scans (REQ-F-06) — le Hub dépend d'ASP.NET Core,
// il vit donc dans Api et non dans Infrastructure (Clean Architecture).
builder.Services.AddSignalR();
builder.Services.AddScoped<IScanEventBroadcaster, ScanEventBroadcaster>();

// Supervision des dépassements de durée (§7) : service de fond périodique.
builder.Services.AddHostedService<OverstayMonitor>();

// Purge des données au-delà de la durée de conservation (§7.3) : service de fond.
builder.Services.AddHostedService<RetentionMonitor>();

// Rate limiting natif .NET 8 sur les endpoints sensibles (section 8.2 du CDC).
// Politique nommée appliquée explicitement sur /api/scan et /api/visits ci-dessous.
var rateLimitPermit = builder.Configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 30;
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("sensitive", opt =>
    {
        opt.PermitLimit = rateLimitPermit;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Applique le schéma Identity (schéma partagé) et amorce rôles + Admin de
    // développement, pour que la connexion soit testable immédiatement. En
    // production, migration et création du premier Admin sont des gestes
    // d'exploitation explicites, pas un effet de bord du démarrage.
    await app.EnsureIdentityReadyAsync();
}

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
app.UseAuthentication();
app.UseTenantResolution();

// Rate limiting désactivable en test d'intégration (la suite sérialisée dépasse
// sinon la politique « sensitive »). Toujours actif en dev/prod.
if (!app.Configuration.GetValue<bool>("RateLimiting:Disabled"))
    app.UseRateLimiter();

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapAuthEndpoints().RequireRateLimiting("auth");
app.MapScanEndpoints().RequireRateLimiting("sensitive");
app.MapVisitEndpoints().RequireRateLimiting("sensitive");
app.MapDashboardEndpoints();
app.MapExclusionEndpoints();
app.MapAuditEndpoints();
app.MapAgentEndpoints();
app.MapAdminEndpoints();
app.MapHub<ScanEventsHub>("/hubs/scan").RequireAuthorization("Dashboard");

app.Run();

return 0;

// Rendu accessible aux tests d'intégration (WebApplicationFactory<Program>).
public partial class Program;
