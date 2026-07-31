using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;

namespace NovAcces.Infrastructure.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le magasin d'identité (schéma partagé « identity »), le service
    /// d'émission de JWT, et les paramètres d'authentification (JWT + clés API
    /// des terminaux agents). L'ajout des SCHÉMAS d'authentification et des
    /// policies RBAC reste côté hôte web (voir NovAcces.Api).
    /// </summary>
    /// <returns>
    /// L'IdentityBuilder, pour que l'hôte web ajoute les fournisseurs de jetons
    /// (AddDefaultTokenProviders) — ceux-ci vivent dans l'assembly ASP.NET Core
    /// Identity (dépendance DataProtection) et ne sont pas référençables depuis
    /// cette couche Infrastructure, volontairement dépourvue du framework web.
    /// </returns>
    public static IdentityBuilder AddNovAccesIdentity(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<NovAccesIdentityDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.")));

        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ITerminalDirectory, TerminalDirectory>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IApplicationAuditLog, ApplicationAuditLog>();

        return services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Politique de mot de passe — durcie (système de sûreté).
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireNonAlphanumeric = true;

                options.User.RequireUniqueEmail = true;

                // Verrouillage après tentatives infructueuses.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<NovAccesIdentityDbContext>();
        // Les fournisseurs de jetons (2FA TOTP, codes de récupération) sont
        // ajoutés par l'hôte web via .AddDefaultTokenProviders() — voir Program.cs.
    }
}
