using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NovAcces.Infrastructure.Auth;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Deux schémas d'authentification :
    ///  - JWT Bearer : utilisateurs du portail web (Hôte / Sûreté / Admin) ;
    ///  - ApiKey : terminaux agents (app MAUI), en-tête X-Api-Key.
    /// Un « policy scheme » par défaut aiguille automatiquement vers le bon
    /// schéma selon la présence de l'en-tête X-Api-Key, afin que HttpContext.User
    /// (et donc le claim SiteId) soit renseigné AVANT la résolution de tenant.
    /// </summary>
    public static IServiceCollection AddNovAccesAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        const string smartScheme = "smart";

        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Jwt:SigningKey manquant (user-secrets en dev, variable d'environnement en prod).");

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = smartScheme;
                options.DefaultChallengeScheme = smartScheme;
            })
            .AddPolicyScheme(smartScheme, "JWT ou ApiKey", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyOptions.HeaderName)
                        ? ApiKeyOptions.Scheme
                        : JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = configuration["Jwt:Issuer"] ?? "NovAcces",
                    ValidateAudience = true,
                    ValidAudience = configuration["Jwt:Audience"] ?? "NovAcces",
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                // SignalR (WebSocket) ne peut pas porter d'en-tête Authorization :
                // le jeton arrive alors en query string "access_token" pour /hubs.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken)
                            && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyOptions.Scheme, _ => { });

        return services;
    }

    /// <summary>
    /// Policies RBAC (section 8.5 du CDC, moindre privilège). Un nom de policy
    /// par regroupement de rôles autorisés sur un endpoint.
    /// </summary>
    public static IServiceCollection AddNovAccesAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(NovAccesRoles.Hote, p => p.RequireRole(NovAccesRoles.Hote))
            .AddPolicy(NovAccesRoles.Agent, p => p.RequireRole(NovAccesRoles.Agent))
            .AddPolicy(NovAccesRoles.Surete, p => p.RequireRole(NovAccesRoles.Surete))
            .AddPolicy(NovAccesRoles.Admin, p => p.RequireRole(NovAccesRoles.Admin))
            // Révocation (REQ-F-09) : Hôte (ses propres QR), Sûreté ou Admin.
            .AddPolicy("RevokeVisit", p => p.RequireRole(
                NovAccesRoles.Hote, NovAccesRoles.Surete, NovAccesRoles.Admin))
            // Dashboard temps réel (REQ-F-06) : Sûreté, Hôte ou Admin.
            .AddPolicy("Dashboard", p => p.RequireRole(
                NovAccesRoles.Surete, NovAccesRoles.Hote, NovAccesRoles.Admin));

        return services;
    }
}
