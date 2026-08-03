using Microsoft.AspNetCore.SignalR.Client;
using NovAcces.Shared.Dtos;

namespace NovAcces.Web.Services;

/// <summary>
/// Connexion SignalR partagée (une seule par session Admin, pas une par page)
/// au canal global "tous sites" — déjà utilisé par AdminOverview pour les
/// scans, réutilisé ici pour signaler qu'un site/agent/terminal/compte a
/// changé (voir IAdminActivityBroadcaster côté API), afin que les tableaux
/// ouverts se rafraîchissent sans rechargement de page. Best-effort : une
/// panne de connexion laisse simplement les tableaux sans mise à jour en
/// direct, jamais indisponibles.
/// </summary>
public sealed class AdminLiveUpdates : IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly AuthState _auth;
    private readonly IWebHostEnvironment _env;
    private HubConnection? _hub;
    private Task? _connecting;

    /// <summary>Se déclenche hors du contexte de synchronisation Blazor — un
    /// abonné doit passer par InvokeAsync avant de toucher son état.</summary>
    public event Action<string>? EntityChanged;

    public AdminLiveUpdates(IConfiguration config, AuthState auth, IWebHostEnvironment env)
    {
        _config = config;
        _auth = auth;
        _env = env;
    }

    public Task EnsureConnectedAsync() => _hub is not null ? Task.CompletedTask : (_connecting ??= ConnectAsync());

    private async Task ConnectAsync()
    {
        var apiBase = _config["Api:BaseUrl"]!;

        var hub = new HubConnectionBuilder()
            .WithUrl($"{apiBase}/hubs/scan?global=1", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_auth.AccessToken);

                if (_env.IsDevelopment())
                {
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler h)
                            h.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                        return handler;
                    };
                    options.WebSocketConfiguration = conf =>
                        conf.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        hub.On<AdminEntityChangedDto>("AdminEntityChanged", dto => EntityChanged?.Invoke(dto.Kind));

        try { await hub.StartAsync(); }
        catch { /* best-effort : les tableaux restent utilisables sans temps réel */ }

        _hub = hub;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
