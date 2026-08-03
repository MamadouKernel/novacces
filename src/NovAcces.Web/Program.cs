using NovAcces.Web.Components;
using NovAcces.Web.Services;

var builder = WebApplication.CreateBuilder(args);

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
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
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
