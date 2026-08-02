using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Auth;

public static class IdentityStartup
{
    /// <summary>
    /// Applique le schéma Identity et amorce les rôles + le compte
    /// SuperAdmin/Admin initial (SeedAdmin:Email/Password). Idempotent —
    /// peut être rejoué sans risque. Appelé automatiquement au démarrage en
    /// développement ; en production, c'est un geste d'exploitation explicite
    /// (voir la commande CLI <c>dotnet NovAcces.Api.dll migrate</c> dans
    /// Program.cs), pas un effet de bord du démarrage du service.
    /// </summary>
    public static async Task EnsureIdentityReadyAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<NovAccesIdentityDbContext>();
        await db.Database.MigrateAsync();

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in NovAccesRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole<Guid>(role));
        }

        var config = sp.GetRequiredService<IConfiguration>();
        var adminEmail = config["SeedAdmin:Email"] ?? "admin@novacces.local";
        var adminPassword = config["SeedAdmin:Password"] ?? "ChangeMoi!2026Dev";
        var adminDisplayName = config["SeedAdmin:DisplayName"] ?? "Admin (dev)";

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                DisplayName = adminDisplayName,
                SiteId = null, // Admin global
                EmailConfirmed = true,
            };

            var created = await users.CreateAsync(admin, adminPassword);
            if (!created.Succeeded)
            {
                app.Logger.LogError("Échec de l'amorçage de l'Admin de dev : {Errors}",
                    string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }

            app.Logger.LogWarning(
                "Compte SuperAdmin de développement amorcé : {Email} (mot de passe par défaut — à changer).", adminEmail);
        }

        // Compte du prestataire : SuperAdmin + Admin (hérite de toutes les
        // capacités Admin). C'est le seul point d'entrée pour ensuite créer les
        // comptes Admin de Sigasécurité via la console (voir AuthEndpoints).
        // Vérifié à CHAQUE démarrage (pas seulement à la création) : une base
        // amorcée avant l'introduction de SuperAdmin doit recevoir le rôle sans
        // ré-créer le compte.
        if (!await users.IsInRoleAsync(admin, NovAccesRoles.SuperAdmin))
            await users.AddToRoleAsync(admin, NovAccesRoles.SuperAdmin);
        if (!await users.IsInRoleAsync(admin, NovAccesRoles.Admin))
            await users.AddToRoleAsync(admin, NovAccesRoles.Admin);
    }
}
