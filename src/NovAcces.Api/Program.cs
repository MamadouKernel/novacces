using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
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

// Rate limiting natif .NET 8 sur les endpoints sensibles (section 8.2 du CDC).
// Politique nommée appliquée explicitement sur /api/scan et /api/visits ci-dessous.
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("sensitive", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
});

// TODO jalon 2 (incrément 2) : 2FA TOTP (Sûreté/Admin), gestion de session/refresh.

var app = builder.Build();

// --- Commande d'administration hors-ligne : provisionnement d'un site ---
//   dotnet run -- provision-site <siteId>
// Crée le schéma PostgreSQL du site, applique le modèle de données et rend le
// journal des scans append-only. Volontairement une commande CLI et NON un
// endpoint HTTP : le provisionnement exécute du DDL sensible et ne doit pas
// être exposé sur le réseau (a fortiori tant que l'authentification/RBAC du
// Jalon 2 n'est pas en place).
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

app.UseHttpsRedirection();

// L'authentification DOIT précéder la résolution de tenant : celle-ci lit le
// claim SiteId du principal authentifié.
app.UseAuthentication();
app.UseTenantResolution();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapAuthEndpoints();
app.MapScanEndpoints().RequireRateLimiting("sensitive");
app.MapVisitEndpoints().RequireRateLimiting("sensitive");
app.MapHub<ScanEventsHub>("/hubs/scan").RequireAuthorization("Dashboard");

app.Run();

return 0;
