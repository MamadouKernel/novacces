using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Auth;

namespace NovAcces.Infrastructure.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le magasin d'identité (schéma partagé « identity »), le service
    /// d'émission de JWT, et les paramètres d'authentification (JWT + clés API
    /// des terminaux agents). L'ajout des SCHÉMAS d'authentification et des
    /// policies RBAC reste côté hôte web (voir NovAcces.Api).
    /// </summary>
    public static IServiceCollection AddNovAccesIdentity(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<NovAccesIdentityDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.")));

        services.AddIdentityCore<ApplicationUser>(options =>
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

        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.Configure<ApiKeyOptions>(configuration.GetSection("ApiKeys"));
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        return services;
    }
}
