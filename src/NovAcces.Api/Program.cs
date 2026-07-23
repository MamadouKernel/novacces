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
