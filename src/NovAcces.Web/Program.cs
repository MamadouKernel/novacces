using Microsoft.AspNetCore.DataProtection;
using NovAcces.Web.Components;
using NovAcces.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Clés DataProtection (antiforgery Blazor Server) persistées sur un volume
// Docker dédié (voir docker-compose.yml, service "web") — même raison que
// côté API : ne pas invalider silencieusement les jetons en cours à chaque
// redéploiement. En développement, le répertoire par défaut suffit.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo("/app/keys"))
        .SetApplicationName("SigasAcces.Web");
}

// Composants Blazor Server (interactifs).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

// État d'authentification (par circuit) + client de l'API NovAcces.
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<NovAccesApiClient>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<AdminLiveUpdates>();

// HttpClient vers l'API. En développement, l'API expose un certificat auto-signé :
// on accepte n'importe quel certificat UNIQUEMENT en dev. En production, le
// certificat sera valide (domaine + TLS réel) et cette exception ne s'applique pas.
var apiBaseUrl = builder.Configuration["Api:BaseUrl"]
    ?? throw new InvalidOperationException("Configuration 'Api:BaseUrl' manquante.");

var apiClientBuilder = builder.Services.AddHttpClient("Api", client =>
    client.BaseAddress = new Uri(apiBaseUrl));

if (builder.Environment.IsDevelopment())
{
    apiClientBuilder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });
}

var app = builder.Build();

// Empreinte du CSS pour le cache-busting (voir AssetVersion et App.razor).
AssetVersion.Initialize(app.Environment.WebRootPath);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// En-têtes de sécurité HTTP (OWASP A05) : anti-sniffing MIME, anti-clickjacking
// (le portail ne doit jamais être embarqué en iframe), pas de fuite de referrer.
//
// CSP : 'unsafe-inline' sur style-src est un compromis assumé — l'app utilise
// largement des attributs style="" inline dans les composants Razor existants ;
// les retirer tous pour un CSP strict est un chantier séparé, pas une correction
// ponctuelle. script-src reste strict (aucun <script> inline dans l'app, tout
// est chargé depuis wwwroot). Google Fonts (App.razor) est la seule ressource
// tierce chargée par le navigateur — tout le reste (API, hub SignalR
// AdminLiveUpdates) est appelé côté serveur par Blazor Server, jamais par le
// navigateur, donc connect-src n'a pas besoin de viser l'origine de l'API.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] =
        "default-src 'self'; "
        + "script-src 'self'; "
        + "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
        + "font-src 'self' https://fonts.gstatic.com; "
        + "img-src 'self' data:; "
        + "connect-src 'self'; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// Sonde de disponibilité (Docker HEALTHCHECK, supervision) — ne renseigne
// aucune donnée d'exploitation, juste que le processus répond.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
