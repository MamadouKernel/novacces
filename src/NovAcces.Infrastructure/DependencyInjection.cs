using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Infrastructure.Security;

namespace NovAcces.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNovAccesInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // --- Base de données : un DbContext, schéma résolu dynamiquement par requête ---
        services.AddDbContext<NovAccesDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.")));

        // --- Tenant courant : une instance par requête HTTP (scoped) ---
        // Enregistré à la fois comme type concret (pour que le middleware
        // puisse appeler Resolve()) et comme interface en lecture seule
        // (pour tout le reste de l'application) — les deux pointent vers
        // la même instance scoped.
        services.AddScoped<CurrentTenant>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<CurrentTenant>());

        // --- Dépôts ---
        services.AddScoped<IVisitRepository, VisitRepository>();
        services.AddScoped<IScanLogRepository, ScanLogRepository>();
        services.AddScoped<IExclusionListService, ExclusionListService>();

        // --- Horloge & signature cryptographique ---
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        services.Configure<QrSigningOptions>(configuration.GetSection("QrSigning"));
        services.AddSingleton<IQrSigningService, Es256QrSigningService>();

        // --- Cas d'usage (Application) ---
        services.AddScoped<ScanQrHandler>();
        services.AddScoped<CreateVisitHandler>();
        services.AddScoped<RevokeVisitHandler>();

        return services;
    }
}
