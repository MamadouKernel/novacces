using Microsoft.AspNetCore.RateLimiting;
using NovAcces.Api.Endpoints;
using NovAcces.Api.Hubs;
using NovAcces.Api.Middleware;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddNovAccesInfrastructure(builder.Configuration);
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

// TODO jalon 2 : builder.Services.AddAuthentication() + AddIdentity<>() avec 2FA TOTP
// (ASP.NET Core Identity, comptes Hôte / Sûreté / Admin, RBAC par policy).

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
}

app.UseHttpsRedirection();
app.UseTenantResolution();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapScanEndpoints().RequireRateLimiting("sensitive");
app.MapVisitEndpoints().RequireRateLimiting("sensitive");
app.MapHub<ScanEventsHub>("/hubs/scan");

app.Run();

return 0;
