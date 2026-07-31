using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovAcces.Infrastructure.Auth;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Auth;

public static class AuthSetup
{
    /// <summary>
    /// Deux schémas d'authentification :
    ///  - JWT Bearer : utilisateurs du portail web (Hôte / Sûreté / Admin) ;
    ///  - ApiKey : terminaux agents (client mobile React Native), en-tête X-Api-Key.
    /// Un « policy scheme » par défaut aiguille automatiquement vers le bon
    /// schéma selon la présence de l'en-tête X-Api-Key, afin que HttpContext.User
    /// (et donc le claim SiteId) soit renseigné AVANT la résolution de tenant.
    /// </summary>
    public static IServiceCollection AddNovAccesAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        const string smartScheme = "smart";

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
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyOptions.Scheme, _ => { });

        // Configuration LAZY du JwtBearer à partir de JwtOptions — MÊME source que
        // l'émission des jetons (JwtTokenService). Lire la clé « eagerly » à
        // l'enregistrement exposait à un décalage signe/valide si la configuration
        // était complétée après coup (cas des tests via WebApplicationFactory).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                if (string.IsNullOrWhiteSpace(jwt.SigningKey))
                    throw new InvalidOperationException(
                        "Jwt:SigningKey manquant (user-secrets en dev, variable d'environnement en prod).");

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                // SignalR (WebSocket) ne peut pas porter d'en-tête Authorization :
                // le jeton arrive alors en query string "access_token" pour /hubs.
                bearer.Events = new JwtBearerEvents
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
            });

        return services;
    }

    /// <summary>
    /// Policies RBAC (section 8.5 du CDC, moindre privilège). Un nom de policy
    /// par regroupement de rôles autorisés sur un endpoint.
    /// </summary>
    public static IServiceCollection AddNovAccesAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(NovAccesRoles.Hote, p => p.RequireRole(NovAccesRoles.Hote, NovAccesRoles.SuperAdmin))
            .AddPolicy(NovAccesRoles.Agent, p => p.RequireRole(NovAccesRoles.Agent, NovAccesRoles.SuperAdmin))
            .AddPolicy(NovAccesRoles.Surete, p => p.RequireRole(NovAccesRoles.Surete, NovAccesRoles.SuperAdmin))
            .AddPolicy(NovAccesRoles.Admin, p => p.RequireRole(NovAccesRoles.Admin, NovAccesRoles.SuperAdmin))
            // SuperAdmin reçoit AUSSI le rôle Admin à la création (voir AuthEndpoints
            // /register) : cette policy sert uniquement à des vérifications strictes
            // qui doivent exclure un Admin simple (ex. création d'un compte Admin).
            .AddPolicy(NovAccesRoles.SuperAdmin, p => p.RequireRole(NovAccesRoles.SuperAdmin))
            // Révocation (REQ-F-09) : Hôte (ses propres QR), Sûreté ou Admin.
            .AddPolicy("RevokeVisit", p => p.RequireRole(
                NovAccesRoles.Hote, NovAccesRoles.Surete, NovAccesRoles.Admin, NovAccesRoles.SuperAdmin))
            // Dashboard temps réel (REQ-F-06) : Sûreté, Hôte ou Admin.
            .AddPolicy("Dashboard", p => p.RequireRole(
                NovAccesRoles.Surete, NovAccesRoles.Hote, NovAccesRoles.Admin, NovAccesRoles.SuperAdmin))
            // Hub du contrat mobile : l'agent reçoit les événements de sa liste locale.
            .AddPolicy("AgentEvents", p => p.RequireRole(
                NovAccesRoles.Agent, NovAccesRoles.Surete, NovAccesRoles.Hote, NovAccesRoles.Admin, NovAccesRoles.SuperAdmin))
            // Liste d'exclusion (REQ-F-11) : gestion réservée à la Sûreté et l'Admin
            // (le motif ne doit pas être exposé aux hôtes — moindre privilège).
            .AddPolicy("ManageExclusions", p => p.RequireRole(
                NovAccesRoles.Surete, NovAccesRoles.Admin, NovAccesRoles.SuperAdmin))

            // Les opérations d'un poste de contrôle restent liées à un terminal
            // enrôlé. Le SuperAdmin bénéficie de la capacité Agent, mais un JWT
            // du portail ne peut pas usurper un terminal : il doit s'authentifier
            // avec la clé API issue de l'enrôlement QR et son claim TerminalId.
            .AddPolicy("AgentTerminal", p => p
                .RequireRole(NovAccesRoles.Agent, NovAccesRoles.SuperAdmin)
                .RequireClaim(NovAccesClaimTypes.TerminalId));
        return services;
    }
}
