using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NovAcces.Application.Abstractions;
using NovAcces.Application.Visits;
using NovAcces.Infrastructure.Notifications;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Infrastructure.Security;

namespace NovAcces.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNovAccesInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // --- Tenant courant : une instance par requête HTTP (scoped) ---
        // Enregistré à la fois comme type concret (pour que le middleware
        // puisse appeler Resolve()) et comme interface en lecture seule
        // (pour tout le reste de l'application) — les deux pointent vers
        // la même instance scoped.
        services.AddScoped<CurrentTenant>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<CurrentTenant>());

        // Provisionnement d'un site (schéma + modèle + journal append-only).
        // Opération d'administration hors requête ; construit ses propres
        // connexions brutes, d'où l'injection directe de la chaîne de connexion.
        // DDL : connexion PROPRIÉTAIRE si elle est configurée, sinon celle du
        // runtime. C'est cette séparation qui rend les REVOKE sur les journaux
        // effectifs (un propriétaire garde des droits implicites sur ses tables).
        // Le repli teste la chaîne VIDE autant que null : le gabarit
        // appsettings.json déclare "PostgresOwner": "" pour se documenter, et
        // une chaîne vide n'est pas une chaîne de connexion utilisable.
        services.AddScoped<ITenantProvisioningService>(sp =>
        {
            var ownerConnection = configuration.GetConnectionString("PostgresOwner");
            if (string.IsNullOrWhiteSpace(ownerConnection))
                ownerConnection = configuration.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(ownerConnection))
                throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.");

            return new TenantProvisioningService(ownerConnection, configuration["Database:ApplicationRole"]);
        });

        // Intercepteur qui positionne le search_path sur le schéma du tenant à
        // chaque ouverture de connexion (cloisonnement multi-tenant robuste au
        // pooling Npgsql — voir TenantSchemaConnectionInterceptor). Scoped car
        // il dépend du tenant de la requête courante.
        services.AddScoped<TenantSchemaConnectionInterceptor>();

        // --- Base de données : un DbContext, schéma résolu dynamiquement par requête ---
        // L'overload (sp, options) permet d'injecter l'intercepteur scoped ci-dessus.
        services.AddDbContext<NovAccesDbContext>((sp, options) =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante."))
                .AddInterceptors(sp.GetRequiredService<TenantSchemaConnectionInterceptor>()));

        // --- Dépôts & frontière transactionnelle ---
        services.AddScoped<IVisitRepository, VisitRepository>();
        services.AddScoped<IScanLogRepository, ScanLogRepository>();
        services.AddScoped<IExclusionListService, ExclusionListService>();
        services.AddScoped<IAdminAuditLog, AdminAuditLog>();
        services.Configure<AgentSecurityOptions>(configuration.GetSection("AgentSecurity"));
        services.AddScoped<IAgentDirectory, AgentDirectory>();
        services.AddScoped<IHostDirectory, Identity.HostDirectory>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // --- Horloge & signature cryptographique ---
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Jours ouvrés (REQ-F-05) : week-ends + jours fériés paramétrables.
        services.Configure<BusinessDayOptions>(configuration.GetSection("BusinessDays"));
        services.AddScoped<IBusinessDayService>(sp =>
            new BusinessDayService(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BusinessDayOptions>>(),
                sp.GetRequiredService<ICurrentTenant>()));

        // Supervision des dépassements (§7) : catalogue de sites + scanner.
        services.AddSingleton<ISiteCatalog, SiteCatalog>();
        services.Configure<Overstay.OverstayOptions>(configuration.GetSection("Overstay"));
        services.AddSingleton<IOverstayScanner, Overstay.OverstayScanner>();

        // Rétention/purge des données personnelles (§7.3) : balayage multi-sites,
        // même orchestration transverse que la supervision.
        services.Configure<Retention.RetentionOptions>(configuration.GetSection("Retention"));
        services.AddSingleton<IDataRetentionService, Retention.DataRetentionService>();

        // Vue consolidée multi-sites (§10).
        services.AddScoped<ISiteOverviewService, SiteOverviewService>();
        services.AddScoped<ISiteTrendsService, SiteTrendsService>();

        services.Configure<QrSigningOptions>(configuration.GetSection("QrSigning"));
        services.AddSingleton<IQrSigningService, Es256QrSigningService>();

        // --- Notifications (REQ-F-03) : email uniquement (WhatsApp abandonné,
        // décision M. Kodjo du 01/08/2026 — voir docs/accord-commercial.md) ---
        services.Configure<SmtpNotificationOptions>(configuration.GetSection("Smtp"));
        services.Configure<NotificationBrandingOptions>(configuration.GetSection("Notifications"));
        services.AddScoped<INotificationService, EmailNotificationService>();

        // --- Cas d'usage (Application) ---
        services.AddScoped<ScanQrHandler>();
        services.AddScoped<CreateVisitHandler>();
        services.AddScoped<UpdateVisitHandler>();
        services.AddScoped<RevokeVisitHandler>();

        return services;
    }
}
