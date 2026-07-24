using System.Net.Http.Headers;
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
        services.AddScoped<ITenantProvisioningService>(sp =>
            new TenantProvisioningService(
                configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("Chaîne de connexion 'Postgres' manquante.")));

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
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // --- Horloge & signature cryptographique ---
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Jours ouvrés (REQ-F-05) : week-ends + jours fériés paramétrables.
        services.Configure<BusinessDayOptions>(configuration.GetSection("BusinessDays"));
        services.AddSingleton<IBusinessDayService, BusinessDayService>();

        services.Configure<QrSigningOptions>(configuration.GetSection("QrSigning"));
        services.AddSingleton<IQrSigningService, Es256QrSigningService>();

        // --- Notifications (REQ-F-03) : WhatsApp Cloud API, repli email ---
        services.Configure<WhatsAppCloudApiOptions>(configuration.GetSection("WhatsApp"));
        services.Configure<SmtpNotificationOptions>(configuration.GetSection("Smtp"));

        services.AddHttpClient<INotificationService, WhatsAppNotificationService>(client =>
        {
            var baseUrl = configuration["WhatsApp:ApiBaseUrl"] ?? "https://graph.facebook.com/v20.0";
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

            var accessToken = configuration["WhatsApp:AccessToken"];
            if (!string.IsNullOrWhiteSpace(accessToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        });

        // --- Cas d'usage (Application) ---
        services.AddScoped<ScanQrHandler>();
        services.AddScoped<CreateVisitHandler>();
        services.AddScoped<RevokeVisitHandler>();

        return services;
    }
}
