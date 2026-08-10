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
            var ownerConnection = PostgresAdminConnectionResolver.Resolve(configuration);
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
        services.AddScoped<IScanConfirmationRequestRepository, ScanConfirmationRequestRepository>();
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

        // Notifications push (WebPush navigateur + Expo mobile) — réveille un
        // client FERMÉ, complète la diffusion SignalR (onglet/app ouverts
        // seulement). AddHttpClient() : IHttpClientFactory pour ExpoPushSender.
        services.AddHttpClient();
        services.Configure<Notifications.WebPushOptions>(configuration.GetSection("WebPush"));
        services.AddSingleton<IWebPushSender, Notifications.WebPushSender>();
        services.AddSingleton<IExpoPushSender, Notifications.ExpoPushSender>();
        services.AddScoped<IOverstayPushNotifier, Notifications.OverstayPushNotifier>();
        services.AddScoped<IConfirmationNotifier, Notifications.ConfirmationNotifier>();

        // Rétention/purge des données personnelles (§7.3) : balayage multi-sites,
        // même orchestration transverse que la supervision.
        services.Configure<Retention.RetentionOptions>(configuration.GetSection("Retention"));
        services.AddSingleton<IDataRetentionService, Retention.DataRetentionService>();

        // Sauvegarde complète de la base (SuperAdmin, §8.5) — audit du
        // 05/08/2026. Singleton : le sémaphore anti-concurrence n'a de sens
        // que partagé pour toute l'application, pas par requête.
        services.Configure<DatabaseBackupOptions>(configuration.GetSection("DatabaseBackup"));
        services.Configure<OffsiteBackupOptions>(configuration.GetSection("DatabaseBackup:Offsite"));
        services.AddSingleton<IBackupOffsiteUploader, S3BackupOffsiteUploader>();
        services.AddSingleton<IDatabaseBackupService, PgDumpDatabaseBackupService>();
        services.AddScoped<IDatabaseHealthService, PostgresDatabaseHealthService>();
        services.AddScoped<IDatabaseQueryService, PostgresReadOnlyQueryService>();
        services.AddScoped<ISiteDataResetService, Identity.SiteDataResetService>();

        // Libellé lisible d'un site (invitations visiteur, §Q ticket enrichi
        // du 05/08/2026) — même convention de configuration que /site/config.
        services.AddSingleton<ISiteDisplayNameProvider, ConfigurationSiteDisplayNameProvider>();

        // Vue consolidée multi-sites (§10).
        services.AddScoped<ISiteOverviewService, SiteOverviewService>();
        services.AddScoped<ISiteTrendsService, SiteTrendsService>();

        services.Configure<QrSigningOptions>(configuration.GetSection("QrSigning"));
        services.AddSingleton<IQrSigningService, Es256QrSigningService>();
        services.AddSingleton<IManualCodeService, ManualCodeService>();

        // --- Notifications (REQ-F-03) : email uniquement (WhatsApp abandonné,
        // décision M. Kodjo du 01/08/2026 — voir docs/accord-commercial.md) ---
        services.Configure<SmtpNotificationOptions>(configuration.GetSection("Smtp"));
        services.Configure<NotificationBrandingOptions>(configuration.GetSection("Notifications"));
        services.AddScoped<INotificationService, EmailNotificationService>();

        // --- Cas d'usage (Application) ---
        services.AddScoped<ScanQrHandler>();
        services.AddScoped<ScanManualCodeHandler>();
        services.AddScoped<CreateVisitHandler>();
        services.AddScoped<UpdateVisitHandler>();
        services.AddScoped<RevokeVisitHandler>();
        services.AddScoped<CreateConfirmationRequestHandler>();
        services.AddScoped<ApproveConfirmationRequestHandler>();
        services.AddScoped<DenyConfirmationRequestHandler>();
        services.AddScoped<OverrideExclusionAndEnterHandler>();

        return services;
    }
}
