using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;

namespace NovAcces.Api.Auth;

public static class IdentityStartup
{
    /// <summary>
    /// Développement uniquement : applique le schéma Identity et amorce les
    /// rôles + un compte Admin de dev, afin que la connexion soit testable sans
    /// étape manuelle. Idempotent.
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

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(adminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                DisplayName = "Admin (dev)",
                SiteId = null, // Admin global
                EmailConfirmed = true,
            };

            var created = await users.CreateAsync(admin, adminPassword);
            if (created.Succeeded)
            {
                await users.AddToRoleAsync(admin, NovAccesRoles.Admin);
                app.Logger.LogWarning(
                    "Compte Admin de développement amorcé : {Email} (mot de passe par défaut — à changer).", adminEmail);
            }
            else
            {
                app.Logger.LogError("Échec de l'amorçage de l'Admin de dev : {Errors}",
                    string.Join("; ", created.Errors.Select(e => e.Description)));
            }
        }
    }
}
