using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
using NovAcces.Infrastructure.Persistence;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Infrastructure.Retention;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

/// <summary>
/// Console d'administration (Admin Sigasécurité, global). Gestion des comptes et
/// provisionnement des sites. La création de compte réutilise /api/auth/register.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin")
            .RequireAuthorization(NovAccesRoles.Admin);

        group.MapGet("/overview", async (
            ISiteOverviewService overview, NovAccesIdentityDbContext identityDb, CancellationToken ct) =>
        {
            var sites = await overview.GetAsync(ct);

            // Absence de ligne = site jamais désactivé (voir SiteRegistration) :
            // actif par défaut, sans qu'il soit nécessaire de rétro-peupler le
            // registre pour les sites provisionnés avant cette fonctionnalité.
            var registrations = await identityDb.Sites
                .Where(s => sites.Select(x => x.SiteId).Contains(s.SiteId))
                .ToDictionaryAsync(s => s.SiteId, s => s, StringComparer.OrdinalIgnoreCase, ct);

            // Un site supprimé (archivé) disparaît de CETTE vue — donc de tous
            // les sélecteurs de site de la console (Comptes/Agents/Terminaux),
            // qui la réutilisent tous. Le schéma et les données ne sont jamais
            // touchés (voir SiteRegistration.DeletedAt) ; seul un SuperAdmin
            // peut encore le consulter via /sites/archived.
            var dto = sites
                .Where(s => !(registrations.TryGetValue(s.SiteId, out var r) && r.DeletedAt is not null))
                .Select(s =>
                {
                    registrations.TryGetValue(s.SiteId, out var reg);
                    return new AdminSiteOverviewDto(
                        s.SiteId, s.OnSite, s.ScansToday,
                        s.TerminalsEnrolled, s.TerminalsActive, s.DegradedScansToday,
                        IsActive: reg?.IsActive ?? true,
                        DeactivatedAt: reg?.DeactivatedAt,
                        DeactivationReason: reg?.DeactivationReason);
                }).ToList();
            return Results.Ok(dto);
        })
        .WithName("AdminOverview")
        .WithSummary("Vue consolidée multi-sites : présents et scans du jour par site.");

        group.MapGet("/trends", async (int? days, ISiteTrendsService trends, CancellationToken ct) =>
        {
            var result = await trends.GetAsync(days ?? 7, ct);
            var dto = new AdminTrendsDto(
                result.Daily.Select(d => new DailyTrendPointDto(
                    d.Date, d.ScansTotal, d.EntriesGranted, d.Exits, d.Denied, d.SecurityEvents)).ToList(),
                result.BySite.Select(s => new SiteActivityTotalDto(s.SiteId, s.ScansTotal)).ToList(),
                result.RepeatedDenials.Select(r => new RepeatedDenialDto(
                    r.SiteId, r.VisitorName, r.Count, r.LastAttemptUtc)).ToList());
            return Results.Ok(dto);
        })
        .WithName("AdminTrends")
        .WithSummary("Tendance d'activité multi-sites sur N jours (1 à 90, défaut 7).");

        group.MapGet("/users", async (
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            CancellationToken ct) =>
        {
            var all = await users.Users.OrderBy(u => u.Email).ToListAsync(ct);
            var callerIsSuperAdmin = NovAccesAuthorizationMatrix.CanViewAllUsers(caller);

            var result = new List<AdminUserDto>(all.Count);
            foreach (var u in all)
            {
                var roles = await users.GetRolesAsync(u);

                // Les comptes SuperAdmin sont invisibles pour les autres rôles.
                // Seul un SuperAdmin peut consulter la flotte complète.
                if (!callerIsSuperAdmin && roles.Contains(NovAccesRoles.SuperAdmin))
                    continue;

                result.Add(new AdminUserDto(u.Id, u.Email!, u.DisplayName, roles.ToList(), u.SiteId, u.TwoFactorEnabled, u.IsDeactivated));
            }

            return Results.Ok(result);
        })
        .WithName("AdminListUsers")
        .WithSummary("Liste les comptes (tous sites).");
        // Désactivation logique d'un compte : aucun DELETE physique. Le contrôle
        // hiérarchique est centralisé dans NovAccesAuthorizationMatrix.
        group.MapPost("/users/{id:guid}/deactivate", async (
            Guid id,
            DeactivateUserRequestDto request,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext identityDb,
            IRefreshTokenService refresh,
            IDateTimeProvider clock,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var reason = request?.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
                return Results.BadRequest(new { error = "Un motif de désactivation (5 à 500 caractères) est obligatoire." });

            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            await using var transaction = await identityDb.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

            if (target.IsDeactivated)
                return Results.Conflict(new { error = "Ce compte est déjà désactivé." });

            var targetRoles = await users.GetRolesAsync(target);
            if (!NovAccesAuthorizationMatrix.CanManageAccount(caller, targetRoles))
                return Results.Json(
                    new { error = "Votre rôle ne permet pas de désactiver ce compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (target.Id == actor.Id && !NovAccesAuthorizationMatrix.CanActOnOwnAccount(caller))
                return Results.Json(
                    new { error = "Vous ne pouvez pas désactiver votre propre compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (targetRoles.Contains(NovAccesRoles.SuperAdmin, StringComparer.Ordinal))
            {
                var activeSuperAdmins = (await users.GetUsersInRoleAsync(NovAccesRoles.SuperAdmin))
                    .Count(u => !u.IsDeactivated);
                if (activeSuperAdmins <= 1)
                    return Results.Conflict(new
                    {
                        error = "Le dernier SuperAdmin actif ne peut pas être désactivé."
                    });
            }

            target.Deactivate(clock.UtcNow);
            var updated = await users.UpdateAsync(target);
            if (!updated.Succeeded)
                return Results.BadRequest(new
                {
                    error = "Désactivation du compte refusée.",
                    details = updated.Errors.Select(e => e.Description)
                });

            await refresh.RevokeAllForSubjectAsync("user", target.Id.ToString(), ct);
            await transaction.CommitAsync(ct);

            // Le journal métier est cloisonné par site. Les comptes globaux
            // restent couverts par le journal technique transversal de la requête.
            if (!string.IsNullOrWhiteSpace(target.SiteId)
                && CurrentTenant.IsValidSiteId(target.SiteId))
            {
                tenant.Resolve(target.SiteId);
                await audit.RecordAsync(
                    AdminAuditAction.AccountDeactivated,
                    actor.Id.ToString(),
                    target.Id.ToString(),
                    $"Compte désactivé. Motif : {reason}",
                    ct);
            }

            await activity.NotifyEntityChangedAsync("accounts", ct);

            return Results.Ok(new
            {
                message = "Compte désactivé. Les données historiques sont conservées.",
                userId = target.Id,
                target.IsDeactivated
            });
        })
        .WithName("AdminDeactivateUser")
        .WithSummary("Désactive logiquement un compte et révoque ses sessions.");

        // Réactivation : même hiérarchie que la désactivation/édition
        // (CanManageAccount) — un Admin peut réactiver les comptes qu'il gère
        // (Hôte/Sûreté/autre Admin), jamais un SuperAdmin. L'auto-réactivation
        // n'a pas besoin d'un garde séparé : un compte désactivé ne peut plus
        // s'authentifier, donc ne peut jamais appeler cet endpoint sur lui-même.
        group.MapPost("/users/{id:guid}/reactivate", async (
            Guid id,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

            var targetRoles = await users.GetRolesAsync(target);
            if (!NovAccesAuthorizationMatrix.CanManageAccount(caller, targetRoles))
                return Results.Json(
                    new { error = "Votre rôle ne permet pas de réactiver ce compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (!target.IsDeactivated)
                return Results.Conflict(new { error = "Ce compte n'est pas désactivé." });

            target.Reactivate();
            var updated = await users.UpdateAsync(target);
            if (!updated.Succeeded)
                return Results.BadRequest(new
                {
                    error = "Réactivation refusée.",
                    details = updated.Errors.Select(e => e.Description)
                });

            if (!string.IsNullOrWhiteSpace(target.SiteId) && CurrentTenant.IsValidSiteId(target.SiteId))
            {
                tenant.Resolve(target.SiteId);
                await audit.RecordAsync(AdminAuditAction.AccountReactivated, actor.Id.ToString(),
                    target.Id.ToString(), "Compte réactivé.", ct);
            }

            await activity.NotifyEntityChangedAsync("accounts", ct);

            return Results.Ok(new { message = "Compte réactivé.", userId = target.Id });
        })
        .WithName("AdminReactivateUser")
        .WithSummary("Réactive un compte désactivé (Admin/SuperAdmin selon hiérarchie).");

        // Édition : nom affiché, rôle, site de rattachement. Même hiérarchie que
        // la désactivation (CanManageAccount) ; promouvoir vers Admin/SuperAdmin
        // exige en plus CanCreateElevatedAccount (SuperAdmin), symétrique de la
        // création de compte (§ /register).
        group.MapPut("/users/{id:guid}", async (
            Guid id,
            UpdateUserRequestDto request,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            ISiteCatalog sites,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var role = request.Role?.Trim();
            if (string.IsNullOrWhiteSpace(role) || !NovAccesRoles.All.Contains(role, StringComparer.Ordinal))
                return Results.BadRequest(new { error = $"Rôle invalide. Attendus : {string.Join(", ", NovAccesRoles.All)}." });

            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160)
                return Results.BadRequest(new { error = "Nom affiché invalide." });

            var email = request.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 256)
                return Results.BadRequest(new { error = "Email invalide." });

            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

            var currentRoles = await users.GetRolesAsync(target);
            if (!NovAccesAuthorizationMatrix.CanManageAccount(caller, currentRoles))
                return Results.Json(
                    new { error = "Votre rôle ne permet pas de modifier ce compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (target.Id == actor.Id && !NovAccesAuthorizationMatrix.CanActOnOwnAccount(caller))
                return Results.Json(
                    new { error = "Vous ne pouvez pas modifier votre propre compte depuis cet écran." },
                    statusCode: StatusCodes.Status403Forbidden);

            // Corrige une erreur de saisie à la création : sans ce contrôle, un
            // email mal tapé bloquerait le compte définitivement (impossible à
            // corriger autrement qu'en recréant le compte).
            if (!string.Equals(email, target.Email, StringComparison.OrdinalIgnoreCase))
            {
                var existingEmail = await users.FindByEmailAsync(email);
                if (existingEmail is not null && existingEmail.Id != target.Id)
                    return Results.Conflict(new { error = "Cet email est déjà utilisé par un autre compte." });
            }

            var isElevatedRole = role is NovAccesRoles.Admin or NovAccesRoles.SuperAdmin;
            // Un Admin peut désormais gérer un autre compte Admin (édition, site,
            // désactivation) sans le repromouvoir explicitement — seule une
            // PROMOTION (rôle élevé nouvellement attribué) reste réservée au
            // SuperAdmin. Garder un rôle déjà élevé inchangé n'est pas une
            // promotion.
            var isPromotion = isElevatedRole && !currentRoles.Contains(role, StringComparer.Ordinal);
            if (isPromotion && !NovAccesAuthorizationMatrix.CanCreateElevatedAccount(caller))
                return Results.Json(
                    new { error = "Seul le SuperAdmin peut attribuer le rôle Admin ou SuperAdmin." },
                    statusCode: StatusCodes.Status403Forbidden);

            // Dernier SuperAdmin actif : ne pas le rétrograder, même par un autre SuperAdmin.
            if (currentRoles.Contains(NovAccesRoles.SuperAdmin, StringComparer.Ordinal) && role != NovAccesRoles.SuperAdmin)
            {
                var activeSuperAdmins = (await users.GetUsersInRoleAsync(NovAccesRoles.SuperAdmin))
                    .Count(u => !u.IsDeactivated);
                if (activeSuperAdmins <= 1)
                    return Results.Conflict(new { error = "Le dernier SuperAdmin actif ne peut pas perdre ce rôle." });
            }

            string? siteId = null;
            if (!isElevatedRole)
            {
                siteId = request.SiteId?.Trim().ToLowerInvariant();
                if (!CurrentTenant.IsValidSiteId(siteId))
                    return Results.BadRequest(new { error = "SiteId requis pour un rôle rattaché à un site." });

                var provisionedSites = await sites.GetSiteIdsAsync(ct);
                if (!provisionedSites.Contains(siteId!, StringComparer.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Le site demandé n'est pas provisionné." });
            }

            if (!string.Equals(email, target.Email, StringComparison.OrdinalIgnoreCase))
            {
                var emailResult = await users.SetEmailAsync(target, email);
                if (!emailResult.Succeeded)
                    return Results.BadRequest(new
                    {
                        error = "Modification de l'email refusée.",
                        details = emailResult.Errors.Select(e => e.Description)
                    });

                var userNameResult = await users.SetUserNameAsync(target, email);
                if (!userNameResult.Succeeded)
                    return Results.BadRequest(new
                    {
                        error = "Modification de l'email refusée.",
                        details = userNameResult.Errors.Select(e => e.Description)
                    });

                // Pas de flux de confirmation d'email dans l'app (EmailConfirmed = true
                // à la création) : on préserve cet invariant après un changement.
                target.EmailConfirmed = true;
            }

            target.DisplayName = displayName;
            target.SiteId = siteId;

            var rolesToRemove = currentRoles.Where(r => r != role && r != NovAccesRoles.Admin).ToList();
            // Admin est retiré séparément : un SuperAdmin en cours de rétrogradation
            // vers Admin doit le CONSERVER, pas juste le perdre puis le regagner.
            if (role != NovAccesRoles.Admin && role != NovAccesRoles.SuperAdmin && currentRoles.Contains(NovAccesRoles.Admin, StringComparer.Ordinal))
                rolesToRemove.Add(NovAccesRoles.Admin);

            if (rolesToRemove.Count > 0)
                await users.RemoveFromRolesAsync(target, rolesToRemove);
            if (!await users.IsInRoleAsync(target, role))
                await users.AddToRoleAsync(target, role);
            if (role == NovAccesRoles.SuperAdmin && !await users.IsInRoleAsync(target, NovAccesRoles.Admin))
                await users.AddToRoleAsync(target, NovAccesRoles.Admin);

            var updated = await users.UpdateAsync(target);
            if (!updated.Succeeded)
                return Results.BadRequest(new
                {
                    error = "Modification refusée.",
                    details = updated.Errors.Select(e => e.Description)
                });

            if (!string.IsNullOrWhiteSpace(target.SiteId) && CurrentTenant.IsValidSiteId(target.SiteId))
            {
                tenant.Resolve(target.SiteId);
                await audit.RecordAsync(AdminAuditAction.AccountUpdated, actor.Id.ToString(),
                    target.Id.ToString(), $"Compte modifié : email « {target.Email} », nom « {displayName} », rôle « {role} », site « {siteId ?? "—"} ».", ct);
            }

            await activity.NotifyEntityChangedAsync("accounts", ct);

            return Results.Ok(new { userId = target.Id, target.Email, target.DisplayName, Role = role, target.SiteId });
        })
        .WithName("AdminUpdateUser")
        .WithSummary("Modifie l'email, le nom, le rôle et le site d'un compte.");

        // Réinitialisation forcée : révoque les sessions, comme une désactivation.
        group.MapPost("/users/{id:guid}/reset-password", async (
            Guid id,
            AdminResetPasswordRequestDto request,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            IRefreshTokenService refresh,
            IAdminAuditLog audit,
            CurrentTenant tenant,
            CancellationToken ct) =>
        {
            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

            var targetRoles = await users.GetRolesAsync(target);
            if (!NovAccesAuthorizationMatrix.CanManageAccount(caller, targetRoles))
                return Results.Json(
                    new { error = "Votre rôle ne permet pas de réinitialiser le mot de passe de ce compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            // Sans mot de passe actuel exigé (contrairement à /me/password) : un
            // Admin ne doit jamais pouvoir réinitialiser LE SIEN par cette voie,
            // même s'il peut désormais gérer d'autres comptes Admin.
            if (target.Id == actor.Id && !NovAccesAuthorizationMatrix.CanActOnOwnAccount(caller))
                return Results.Json(
                    new { error = "Utilisez le changement de mot de passe depuis votre profil." },
                    statusCode: StatusCodes.Status403Forbidden);

            var resetToken = await users.GeneratePasswordResetTokenAsync(target);
            var result = await users.ResetPasswordAsync(target, resetToken, request.NewPassword);
            if (!result.Succeeded)
                return Results.BadRequest(new
                {
                    error = "Mot de passe refusé.",
                    details = result.Errors.Select(e => e.Description)
                });

            await refresh.RevokeAllForSubjectAsync("user", target.Id.ToString(), ct);

            if (!string.IsNullOrWhiteSpace(target.SiteId) && CurrentTenant.IsValidSiteId(target.SiteId))
            {
                tenant.Resolve(target.SiteId);
                await audit.RecordAsync(AdminAuditAction.AccountPasswordReset, actor.Id.ToString(),
                    target.Id.ToString(), "Mot de passe réinitialisé par un administrateur.", ct);
            }

            return Results.Ok(new { message = "Mot de passe réinitialisé.", userId = target.Id });
        })
        .WithName("AdminResetUserPassword")
        .WithSummary("Réinitialise le mot de passe d'un compte (Admin/SuperAdmin selon hiérarchie).");

        // Suppression logique (archivage) : n'agit que sur un compte déjà
        // désactivé (discipline en deux temps, comme pour un agent). Même
        // hiérarchie que la désactivation (CanManageAccount/CanActOnOwnAccount).
        group.MapPost("/users/{id:guid}/delete", async (
            Guid id,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            IDateTimeProvider clock,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

            var targetRoles = await users.GetRolesAsync(target);
            if (!NovAccesAuthorizationMatrix.CanManageAccount(caller, targetRoles))
                return Results.Json(
                    new { error = "Votre rôle ne permet pas de supprimer ce compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (target.Id == actor.Id && !NovAccesAuthorizationMatrix.CanActOnOwnAccount(caller))
                return Results.Json(
                    new { error = "Vous ne pouvez pas supprimer votre propre compte." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (!target.IsDeactivated)
                return Results.Conflict(new { error = "Désactivez le compte avant de le supprimer." });

            target.Delete(clock.UtcNow, actor.Id.ToString());
            var updated = await users.UpdateAsync(target);
            if (!updated.Succeeded)
                return Results.BadRequest(new
                {
                    error = "Suppression refusée.",
                    details = updated.Errors.Select(e => e.Description)
                });

            if (!string.IsNullOrWhiteSpace(target.SiteId) && CurrentTenant.IsValidSiteId(target.SiteId))
            {
                tenant.Resolve(target.SiteId);
                await audit.RecordAsync(AdminAuditAction.AccountDeleted, actor.Id.ToString(),
                    target.Id.ToString(), "Compte supprimé (archivé).", ct);
            }

            await activity.NotifyEntityChangedAsync("accounts", ct);

            return Results.Ok(new { message = "Compte supprimé (archivé).", userId = target.Id });
        })
        .WithName("AdminDeleteUser")
        .WithSummary("Supprime (archive) un compte déjà désactivé — email réutilisable ensuite.");

        group.MapGet("/users/archived", async (
            NovAccesIdentityDbContext identityDb,
            CancellationToken ct) =>
        {
            var archived = await identityDb.Users
                .IgnoreQueryFilters()
                .Where(u => u.DeletedAt != null)
                .OrderByDescending(u => u.DeletedAt)
                .Select(u => new ArchivedAccountSummaryDto(u.Id, u.Email!, u.DisplayName, u.DeletedAt!.Value, u.DeletedBy))
                .ToListAsync(ct);
            return Results.Ok(archived);
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("AdminListArchivedUsers")
        .WithSummary("Liste les comptes supprimés (archivés), en lecture seule (SuperAdmin uniquement).");

        // Provisionnement d'un site depuis la console : désormais possible en HTTP
        // car protégé par le rôle Admin (auth en place). Le service reste aussi
        // disponible en CLI pour l'exploitation (dotnet run -- provision-site).
        group.MapPost("/sites", async (
            ProvisionSiteRequestDto request,
            ITenantProvisioningService provisioning,
            ISiteCatalog sites,
            ILogger<Program> logger,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var siteId = request.SiteId?.Trim().ToLowerInvariant();
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide (a-z, 0-9, _ ; max 40)." });

            // Le provisionnement fait du DDL brut hors du modèle EF habituel
            // (schéma, triggers, GRANT) : contrairement au reste de la console,
            // une panne ici (base de données injoignable, migration serveur pas
            // à jour, rôle applicatif mal configuré) ne doit pas remonter comme
            // une exception muette — l'opérateur a besoin de savoir QUOI a cassé
            // sans qu'on lui expose la trace complète (détails journalisés).
            try
            {
                await provisioning.ProvisionAsync(siteId!, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du provisionnement du site {SiteId}.", siteId);
                return Results.Problem(
                    detail: "Le provisionnement a échoué côté serveur (base de données ou configuration). " +
                            "L'erreur a été journalisée ; contactez le support technique si le problème persiste.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Le catalogue met l'existence des sites en cache pour ne pas
            // interroger la base à chaque requête : sans invalidation, le site
            // qu'on vient de créer resterait « inconnu » le temps du TTL.
            sites.Invalidate();
            await activity.NotifyEntityChangedAsync("sites", ct);

            return Results.Ok(new { message = $"Site '{siteId}' provisionné." });
        })
        .WithName("AdminProvisionSite")
        .WithSummary("Provisionne un nouveau site (schéma + modèle + journal append-only).");

        // Désactivation : coupe l'accès au site (contrat non reconduit) SANS
        // toucher aux données — jamais de DROP SCHEMA depuis un endpoint HTTP.
        // Même hiérarchie de motif que la désactivation d'un compte.
        group.MapPost("/sites/{siteId}/deactivate", async (
            string siteId,
            DeactivateSiteRequestDto request,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext identityDb,
            ISiteCatalog sites,
            IDateTimeProvider clock,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            siteId = siteId.Trim().ToLowerInvariant();
            var reason = request?.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
                return Results.BadRequest(new { error = "Un motif de désactivation (5 à 500 caractères) est obligatoire." });

            if (!await sites.ExistsAsync(siteId, ct))
                return Results.NotFound(new { error = "Site introuvable ou non provisionné." });

            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var registration = await identityDb.Sites.FindAsync(new object?[] { siteId }, ct);
            if (registration is null)
            {
                registration = SiteRegistration.Create(siteId, clock.UtcNow);
                identityDb.Sites.Add(registration);
            }
            else if (!registration.IsActive)
            {
                return Results.Conflict(new { error = "Ce site est déjà désactivé." });
            }

            registration.Deactivate(clock.UtcNow, actor.Id.ToString(), reason);
            await identityDb.SaveChangesAsync(ct);

            // Sans invalidation, TenantResolutionMiddleware continuerait à
            // servir ce site jusqu'à expiration du cache (30s) — trop long pour
            // une action de sûreté censée être immédiate.
            sites.Invalidate();

            tenant.Resolve(siteId);
            await audit.RecordAsync(
                AdminAuditAction.SiteDeactivated, actor.Id.ToString(), siteId,
                $"Site désactivé. Motif : {reason}", ct);

            await activity.NotifyEntityChangedAsync("sites", ct);

            return Results.Ok(new
            {
                message = "Site désactivé. Les données sont conservées, l'accès est coupé.",
                siteId,
                isActive = false
            });
        })
        .WithName("AdminDeactivateSite")
        .WithSummary("Désactive un site (contrat non reconduit) : coupe l'accès sans supprimer les données.");

        // Réactivation : réservée au SuperAdmin, même asymétrie que pour un compte.
        group.MapPost("/sites/{siteId}/reactivate", async (
            string siteId,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext identityDb,
            ISiteCatalog sites,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            siteId = siteId.Trim().ToLowerInvariant();

            if (!await sites.ExistsAsync(siteId, ct))
                return Results.NotFound(new { error = "Site introuvable ou non provisionné." });

            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var registration = await identityDb.Sites.FindAsync(new object?[] { siteId }, ct);
            if (registration is null || registration.IsActive)
                return Results.Conflict(new { error = "Ce site n'est pas désactivé." });

            registration.Reactivate();
            await identityDb.SaveChangesAsync(ct);

            sites.Invalidate();

            tenant.Resolve(siteId);
            await audit.RecordAsync(
                AdminAuditAction.SiteReactivated, actor.Id.ToString(), siteId, "Site réactivé.", ct);

            await activity.NotifyEntityChangedAsync("sites", ct);

            return Results.Ok(new { message = "Site réactivé.", siteId, isActive = true });
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("AdminReactivateSite")
        .WithSummary("Réactive un site désactivé (SuperAdmin uniquement).");

        // Suppression logique (archivage) : n'agit que sur un site déjà
        // désactivé (discipline en deux temps). Contrairement à un agent ou
        // un compte, le SiteId N'EST PAS réutilisable ensuite (voir
        // SiteRegistration.Delete) — le schéma et les données restent intacts,
        // seule la visibilité dans les listes change.
        group.MapPost("/sites/{siteId}/delete", async (
            string siteId,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            NovAccesIdentityDbContext identityDb,
            ISiteCatalog sites,
            IDateTimeProvider clock,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            siteId = siteId.Trim().ToLowerInvariant();

            if (!await sites.ExistsAsync(siteId, ct))
                return Results.NotFound(new { error = "Site introuvable ou non provisionné." });

            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var registration = await identityDb.Sites.FindAsync(new object?[] { siteId }, ct);
            if (registration is null || registration.IsActive)
                return Results.Conflict(new { error = "Désactivez le site avant de le supprimer." });

            registration.Delete(clock.UtcNow, actor.Id.ToString());
            await identityDb.SaveChangesAsync(ct);

            tenant.Resolve(siteId);
            await audit.RecordAsync(
                AdminAuditAction.SiteDeleted, actor.Id.ToString(), siteId, "Site supprimé (archivé).", ct);

            await activity.NotifyEntityChangedAsync("sites", ct);

            return Results.Ok(new { message = "Site supprimé (archivé).", siteId });
        })
        .WithName("AdminDeleteSite")
        .WithSummary("Supprime (archive) un site déjà désactivé — l'identifiant reste réservé, schéma et données intacts.");

        group.MapGet("/sites/archived", async (NovAccesIdentityDbContext identityDb, CancellationToken ct) =>
        {
            var archived = await identityDb.Sites
                .Where(s => s.DeletedAt != null)
                .OrderByDescending(s => s.DeletedAt)
                .Select(s => new ArchivedSiteSummaryDto(
                    s.SiteId, s.DeletedAt!.Value, s.DeletedBy, s.DeactivationReason))
                .ToListAsync(ct);
            return Results.Ok(archived);
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("AdminListArchivedSites")
        .WithSummary("Liste les sites supprimés (archivés), en lecture seule (SuperAdmin uniquement).");

        // Export complet d'un site (visites, journal des scans, audit
        // d'administration) — à la demande, en formats ouverts (CSV zippé),
        // pour la clause contractuelle de restitution des données
        // (accord-commercial.md) à la fin d'une relation client. Fonctionne
        // aussi sur un site encore actif (utile avant même de désactiver).
        // Ne dépend PAS de TenantResolutionMiddleware (comme provision/
        // deactivate/reactivate) : le tenant est résolu manuellement dans un
        // scope dédié, donc jamais bloqué par le statut actif/inactif.
        group.MapGet("/sites/{siteId}/export", async (
            string siteId,
            IServiceScopeFactory scopeFactory,
            ISiteCatalog sites,
            CancellationToken ct) =>
        {
            siteId = siteId.Trim().ToLowerInvariant();
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });
            if (!await sites.ExistsAsync(siteId, ct))
                return Results.NotFound(new { error = "Site introuvable ou non provisionné." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var db = scope.ServiceProvider.GetRequiredService<NovAccesDbContext>();

            var visits = await db.Visits.AsNoTracking().OrderBy(v => v.CreatedAt).ToListAsync(ct);
            var scans = await db.ScanLogs.AsNoTracking().OrderBy(s => s.Timestamp).ToListAsync(ct);
            var auditEntries = await db.AdminAudit.AsNoTracking().OrderBy(a => a.Timestamp).ToListAsync(ct);

            using var zipStream = new MemoryStream();
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteCsvEntryAsync(zip, "visites.csv",
                    "Id;VisiteurNom;VisiteurSociete;VisiteurTelephone;VisiteurEmail;Motif;HoteId;Mode;Statut;PlanifieLe;DureePrevueMin;PresentSurSite;ArriveLe;PartiLe;CycleComplet;CreeLe;RevoqueParS;RevoqueLe",
                    visits.Select(v => string.Join(';', new[]
                    {
                        v.Id.ToString(), Csv(v.VisitorName), Csv(v.VisitorCompany), Csv(v.VisitorPhone ?? ""),
                        Csv(v.VisitorEmail ?? ""), Csv(v.Motif), Csv(v.HostUserId), v.Mode.ToString(), v.Status.ToString(),
                        v.ScheduledAt?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                        v.PlannedDurationMinutes.ToString(CultureInfo.InvariantCulture),
                        v.IsOnSite ? "oui" : "non",
                        v.CheckedInAt?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                        v.CheckedOutAt?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                        v.HasCompletedCycle ? "oui" : "non",
                        v.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                        Csv(v.RevokedBy ?? ""),
                        v.RevokedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                    })), ct);

                await WriteCsvEntryAsync(zip, "journal-scans.csv",
                    "Horodatage;Visiteur;Agent;Direction;Autorise;Sortie;EvenementSecurite;MotifRefus;ModeDegrade;Detail",
                    scans.Select(s => string.Join(';', new[]
                    {
                        s.Timestamp.ToString("o", CultureInfo.InvariantCulture), Csv(s.VisitorName), Csv(s.AgentId),
                        s.Direction.ToString(), s.WasGranted ? "oui" : "non", s.WasCheckOut ? "oui" : "non",
                        s.IsSecurityEvent ? "oui" : "non", s.DenialReason?.ToString() ?? "",
                        s.RecordedInDegradedMode ? "oui" : "non", Csv(s.Detail),
                    })), ct);

                await WriteCsvEntryAsync(zip, "audit-administration.csv",
                    "Horodatage;Acteur;Action;Cible;Detail",
                    auditEntries.Select(a => string.Join(';', new[]
                    {
                        a.Timestamp.ToString("o", CultureInfo.InvariantCulture), Csv(a.Actor), a.Action.ToString(),
                        Csv(a.TargetId ?? ""), Csv(a.Detail),
                    })), ct);
            }

            return Results.File(zipStream.ToArray(), "application/zip",
                $"export-{siteId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}.zip");
        })
        .WithName("AdminExportSite")
        .WithSummary("Exporte les données d'un site (visites, journal, audit) en CSV zippé — formats ouverts.");

        // --- Rétention / purge des données personnelles (§7.3) ---

        group.MapGet("/retention", (IOptions<RetentionOptions> options) =>
        {
            var o = options.Value;
            return Results.Ok(new RetentionStatusDto(
                o.Enabled, o.VisitRetentionDays, o.JournalRetentionDays, o.RunIntervalHours));
        })
        .WithName("AdminRetentionStatus")
        .WithSummary("Politique de rétention en vigueur (conservation, anonymisation, intervalle).");

        // Déclenchement manuel d'une passe de rétention (en plus de la passe
        // automatique du RetentionMonitor). Action privilégiée : chaque site
        // traité est inscrit à son journal d'audit inaltérable.
        group.MapPost("/retention/run", async (IDataRetentionService retention, CancellationToken ct) =>
        {
            var results = await retention.PurgeOnceAsync(ct);
            var dto = new RetentionRunResultDto(
                results.Sites.Sum(r => r.VisitsPurged),
                results.Sites.Sum(r => r.ScanLogsAnonymized),
                results.Sites.Select(r => new SitePurgeDto(r.SiteId, r.VisitsPurged, r.ScanLogsAnonymized)).ToList(),
                results.ApplicationAuditPurged,
                results.RefreshSessionsPurged);
            return Results.Ok(dto);
        })
        .WithName("AdminRetentionRun")
        .WithSummary("Déclenche une passe immédiate de rétention : purge + anonymisation des journaux (§7.3).");

        // --- Agents (prise de poste matricule + PIN) ---
        // Création réservée à l'Admin. Le PIN est haché côté annuaire. On résout
        // le tenant du site cible dans un scope dédié (comme le provisionnement).
        group.MapPost("/agents", async (
            CreateAgentRequestDto request,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var siteId = request.SiteId?.Trim().ToLowerInvariant();
            var matricule = request.Matricule?.Trim();
            var displayName = request.DisplayName?.Trim();
            var pin = request.Pin?.Trim();
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });
            if (string.IsNullOrWhiteSpace(matricule) || matricule.Length > 80
                || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160)
                return Results.BadRequest(new { error = "Matricule et nom requis." });
            if (string.IsNullOrWhiteSpace(pin) || pin.Length is < 4 or > 8 || !pin.All(char.IsDigit))
                return Results.BadRequest(new { error = "PIN numérique de 4 à 8 chiffres requis." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId!);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            var activity = scope.ServiceProvider.GetRequiredService<IAdminActivityBroadcaster>();

            try
            {
                await agents.AddAsync(matricule!, displayName!, pin!, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            await audit.RecordAsync(AdminAuditAction.AgentCreated, user.HostIdentifier(), matricule!,
                $"Création de l'agent « {displayName} » (matricule {matricule}).", ct);
            await activity.NotifyEntityChangedAsync("agents", ct);

            return Results.Ok(new { message = $"Agent {matricule} créé pour le site {siteId}." });
        })
        .WithName("AdminCreateAgent")
        .WithSummary("Crée un agent (matricule + PIN) pour la prise de poste sur un site.");

        group.MapGet("/agents/{siteId}", async (
            string siteId,
            IServiceScopeFactory scopeFactory,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();

            var list = await agents.ListAsync(ct);
            return Results.Ok(list.Select(a => new AgentSummaryDto(a.Matricule, a.DisplayName, a.IsActive)).ToList());
        })
        .WithName("AdminListAgents")
        .WithSummary("Liste les agents (matricule + nom) d'un site.");

        // Désactivation : un agent réaffecté sur un autre site (ou parti) ne
        // doit pas garder un PIN valide indéfiniment sur ce site-ci.
        group.MapPost("/agents/{siteId}/{matricule}/deactivate", async (
            string siteId,
            string matricule,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            var activity = scope.ServiceProvider.GetRequiredService<IAdminActivityBroadcaster>();

            var deactivated = await agents.DeactivateAsync(matricule, ct);
            if (!deactivated)
                return Results.NotFound(new { error = $"Agent {matricule} introuvable sur le site {siteId}." });

            await audit.RecordAsync(AdminAuditAction.AgentDeactivated, user.HostIdentifier(), matricule,
                $"Désactivation de l'agent {matricule} sur le site {siteId}.", ct);
            await activity.NotifyEntityChangedAsync("agents", ct);

            return Results.Ok(new { message = $"Agent {matricule} désactivé sur le site {siteId}." });
        })
        .WithName("AdminDeactivateAgent")
        .WithSummary("Désactive un agent sur un site (départ ou réaffectation vers un autre site).");

        // Réactivation : retour d'un agent sur un site qu'il avait quitté — il
        // reprend son matricule d'origine plutôt que d'en obtenir un nouveau.
        group.MapPost("/agents/{siteId}/{matricule}/reactivate", async (
            string siteId,
            string matricule,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            var activity = scope.ServiceProvider.GetRequiredService<IAdminActivityBroadcaster>();

            bool reactivated;
            try
            {
                reactivated = await agents.ReactivateAsync(matricule, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Même garde-fou qu'à la création : un retour sur ce site ne
                // doit pas laisser le matricule actif ailleurs en même temps
                // (voir IAgentRegistry, appelé par AgentDirectory).
                return Results.Conflict(new { error = ex.Message });
            }

            if (!reactivated)
                return Results.NotFound(new { error = $"Agent {matricule} introuvable sur le site {siteId}." });

            await audit.RecordAsync(AdminAuditAction.AgentReactivated, user.HostIdentifier(), matricule,
                $"Réactivation de l'agent {matricule} sur le site {siteId}.", ct);
            await activity.NotifyEntityChangedAsync("agents", ct);

            return Results.Ok(new { message = $"Agent {matricule} réactivé sur le site {siteId}." });
        })
        .WithName("AdminReactivateAgent")
        .WithSummary("Réactive un agent sur un site qu'il avait quitté (retour d'affectation).");

        // Suppression logique (archivage) : n'agit que sur un agent déjà
        // désactivé (discipline en deux temps, comme la réaffectation). La
        // ligne reste en base pour la traçabilité ; le matricule redevient
        // disponible pour un nouvel agent (index unique partiel, voir
        // AgentConfiguration).
        group.MapPost("/agents/{siteId}/{matricule}/delete", async (
            string siteId,
            string matricule,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            var activity = scope.ServiceProvider.GetRequiredService<IAdminActivityBroadcaster>();

            bool deleted;
            try
            {
                deleted = await agents.DeleteAsync(matricule, user.HostIdentifier(), ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            if (!deleted)
                return Results.NotFound(new { error = $"Agent {matricule} introuvable sur le site {siteId}." });

            await audit.RecordAsync(AdminAuditAction.AgentDeleted, user.HostIdentifier(), matricule,
                $"Suppression (archivage) de l'agent {matricule} sur le site {siteId}.", ct);
            await activity.NotifyEntityChangedAsync("agents", ct);

            return Results.Ok(new { message = $"Agent {matricule} supprimé sur le site {siteId}." });
        })
        .WithName("AdminDeleteAgent")
        .WithSummary("Supprime (archive) un agent déjà désactivé — matricule réutilisable ensuite.");

        group.MapGet("/agents/{siteId}/archived", async (
            string siteId,
            IServiceScopeFactory scopeFactory,
            CancellationToken ct) =>
        {
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });

            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var agents = scope.ServiceProvider.GetRequiredService<IAgentDirectory>();

            var list = await agents.ListArchivedAsync(ct);
            return Results.Ok(list
                .Select(a => new ArchivedAgentSummaryDto(a.Matricule, a.DisplayName, a.DeletedAt, a.DeletedBy))
                .ToList());
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("AdminListArchivedAgents")
        .WithSummary("Liste les agents supprimés (archivés) d'un site, en lecture seule (SuperAdmin uniquement).");

        // Réaffectation : désactive d'abord la source, puis crée sur la cible.
        // La garantie « jamais actif aux deux endroits » vient de la
        // contrainte d'unicité du registre global (IAgentRegistry, appelé par
        // AgentDirectory) — pas d'une vérification applicative racée. En cas
        // d'échec de la création sur la cible, on compense en réactivant la
        // source : le pire cas est un agent bloqué partout le temps de la
        // compensation, jamais actif aux deux endroits à la fois.
        group.MapPost("/agents/{siteId}/{matricule}/reassign", async (
            string siteId,
            string matricule,
            ReassignAgentRequestDto request,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var targetSiteId = request.TargetSiteId?.Trim().ToLowerInvariant();
            var pin = request.Pin?.Trim();

            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide." });
            if (!CurrentTenant.IsValidSiteId(targetSiteId))
                return Results.BadRequest(new { error = "Identifiant de site cible invalide." });
            if (string.Equals(siteId, targetSiteId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Le site cible doit être différent du site source." });
            if (string.IsNullOrWhiteSpace(pin) || pin.Length is < 4 or > 8 || !pin.All(char.IsDigit))
                return Results.BadRequest(new { error = "PIN numérique de 4 à 8 chiffres requis." });

            using var sourceScope = scopeFactory.CreateScope();
            sourceScope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var sourceAgents = sourceScope.ServiceProvider.GetRequiredService<IAgentDirectory>();
            var sourceAudit = sourceScope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            var sourceActivity = sourceScope.ServiceProvider.GetRequiredService<IAdminActivityBroadcaster>();

            var existing = (await sourceAgents.ListAsync(ct)).FirstOrDefault(a => a.Matricule == matricule);
            if (existing is null || !existing.IsActive)
                return Results.NotFound(new { error = $"Agent {matricule} introuvable ou déjà inactif sur le site {siteId}." });
            var displayName = existing.DisplayName;

            var deactivated = await sourceAgents.DeactivateAsync(matricule, ct);
            if (!deactivated)
                return Results.Conflict(new { error = "Échec de la désactivation sur le site source, réaffectation annulée." });

            await sourceAudit.RecordAsync(AdminAuditAction.AgentDeactivated, user.HostIdentifier(), matricule,
                $"Désactivation de l'agent {matricule} sur le site {siteId} (réaffectation vers {targetSiteId}).", ct);

            using var targetScope = scopeFactory.CreateScope();
            targetScope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(targetSiteId!);
            var targetAgents = targetScope.ServiceProvider.GetRequiredService<IAgentDirectory>();

            try
            {
                await targetAgents.AddAsync(matricule, displayName, pin!, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Compensation : la désactivation source est déjà committée,
                // on la réactive pour ne pas laisser l'agent bloqué partout.
                await sourceAgents.ReactivateAsync(matricule, ct);
                await sourceAudit.RecordAsync(AdminAuditAction.AgentReactivated, user.HostIdentifier(), matricule,
                    $"Réactivation automatique de l'agent {matricule} sur {siteId} : échec de création sur {targetSiteId} ({ex.Message}).", ct);
                await sourceActivity.NotifyEntityChangedAsync("agents", ct);
                return Results.Conflict(new
                {
                    error = $"Création sur « {targetSiteId} » refusée : {ex.Message} " +
                             $"L'agent reste actif sur « {siteId} »."
                });
            }

            var targetAudit = targetScope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            var targetActivity = targetScope.ServiceProvider.GetRequiredService<IAdminActivityBroadcaster>();
            await targetAudit.RecordAsync(AdminAuditAction.AgentCreated, user.HostIdentifier(), matricule,
                $"Création de l'agent « {displayName} » (matricule {matricule}) sur {targetSiteId} (réaffecté depuis {siteId}).", ct);

            await sourceActivity.NotifyEntityChangedAsync("agents", ct);
            await targetActivity.NotifyEntityChangedAsync("agents", ct);

            return Results.Ok(new { message = $"Agent {matricule} réaffecté de « {siteId} » vers « {targetSiteId} »." });
        })
        .WithName("AdminReassignAgent")
        .WithSummary("Réaffecte un agent vers un autre site de façon atomique (désactive la source avant de créer la cible, avec compensation si la création échoue).");

        // --- Terminaux (enrôlement) ---
        // Contrairement aux agents, un terminal ne vit dans le schéma d'AUCUN
        // site (il peut en servir plusieurs) : ITerminalDirectory est adossé au
        // schéma partagé « identity », pas besoin de résoudre un tenant ici.
        group.MapPost("/terminals", async (
            CreateTerminalRequestDto request,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ISiteCatalog sites,
            ClaimsPrincipal user,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var label = request.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label) || label.Length > 120)
                return Results.BadRequest(new { error = "Libellé requis (120 caractères maximum)." });

            var siteIds = (request.SiteIds ?? Array.Empty<string>())
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            if (siteIds.Count == 0)
                return Results.BadRequest(new { error = "Au moins un site requis." });

            if (siteIds.Any(s => !CurrentTenant.IsValidSiteId(s)))
                return Results.BadRequest(new { error = "Un des identifiants de site est invalide." });

            var provisioned = await sites.GetSiteIdsAsync(ct);
            if (siteIds.Any(site => !provisioned.Contains(site, StringComparer.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "Tous les sites doivent être provisionnés avant l'enrôlement du terminal." });

            var id = await terminals.CreateAsync(label!, siteIds, ct);

            // Le terminal n'appartient à aucun tenant en particulier (il peut en
            // servir plusieurs) : l'action est inscrite dans le journal d'audit
            // de CHAQUE site concerné, pour que la sûreté/l'admin de ce site
            // voie qu'un terminal a été autorisé à le servir.
            await RecordOnEachSiteAsync(scopeFactory, siteIds,
                AdminAuditAction.TerminalCreated, user.HostIdentifier(), id.ToString(),
                $"Terminal « {label} » enrôlé pour {string.Join(", ", siteIds)}.", ct);
            await activity.NotifyEntityChangedAsync("terminals", ct);

            return Results.Created($"/api/admin/terminals/{id:D}", new CreateTerminalResponseDto(id, label!));
        })
        .WithName("AdminCreateTerminal")
        .WithSummary("Provisionne un terminal ; son activation se fait par ticket QR temporaire.");

        // Ticket QR temporaire : aucune clé API n'est affichée dans le Web.
        group.MapPost("/terminals/{id:guid}/enrollment-ticket", async (
            Guid id,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            HttpRequest http,
            IConfiguration configuration,
            IHostEnvironment environment,
            CancellationToken ct) =>
        {
            var minutes = configuration.GetValue<double?>("Enrollment:TicketLifetimeMinutes") ?? 60d;
            minutes = Math.Clamp(minutes, 1d, 60d);
            var ticket = await terminals.CreateEnrollmentTicketAsync(
                id, user.HostIdentifier(), TimeSpan.FromMinutes(minutes), ct);
            if (ticket is null)
                return Results.NotFound(new { error = "Terminal introuvable ou révoqué." });

            var configuredBaseUrl = configuration["Api:PublicBaseUrl"]?.Trim();
            string publicBaseUrl;
            if (string.IsNullOrWhiteSpace(configuredBaseUrl))
            {
                if (environment.IsProduction())
                    return Results.Problem("Api:PublicBaseUrl doit être configurée en production.", statusCode: StatusCodes.Status500InternalServerError);
                publicBaseUrl = $"{http.Scheme}://{http.Host}";
            }
            else if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("https" or "http")
                || !string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment))
            {
                return Results.Problem("Api:PublicBaseUrl doit être une URL absolue sans query ni fragment.", statusCode: StatusCodes.Status500InternalServerError);
            }
            else if (environment.IsProduction() && !string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Api:PublicBaseUrl doit utiliser HTTPS en production.", statusCode: StatusCodes.Status500InternalServerError);
            }
            else
            {
                publicBaseUrl = configuredBaseUrl.TrimEnd('/');
            }
            // Format JSON, PAS l'ancien schéma d'URI "novacces://enroll?..." (hérité
            // du prototype MAUI abandonné) : l'app agent réelle est en React Native
            // (dépôt sigasacces-mobile), et son parseur de ticket (parseEnrollmentTicket)
            // n'accepte qu'un ticket brut ou ce JSON — jamais l'URI, qu'il aurait pris
            // tel quel pour un "ticket" invalide, faisant échouer l'activation à
            // chaque scan. Garder les deux formats synchronisés si l'un change.
            var qrPayload = JsonSerializer.Serialize(new { ticket = ticket.Ticket, baseUrl = publicBaseUrl });

            await RecordOnEachSiteAsync(scopeFactory, ticket.SiteIds,
                AdminAuditAction.EnrollmentTicketCreated, user.HostIdentifier(), ticket.TerminalId.ToString(),
                $"Ticket QR temporaire créé pour le terminal « {ticket.Label} » (expiration {ticket.ExpiresAt:O}).", ct);

            return Results.Ok(new EnrollmentTicketResponseDto(
                ticket.TerminalId, ticket.Label, ticket.SiteIds, qrPayload, ticket.ManualCode, ticket.ExpiresAt));
        })
        .RequireRateLimiting("sensitive")
        .WithName("CreateTerminalEnrollmentTicket")
        .WithSummary("Génère un QR d'enrôlement temporaire, à usage unique.");

        group.MapGet("/terminals", async (ITerminalDirectory terminals, CancellationToken ct) =>
        {
            var list = await terminals.ListAsync(ct);
            return Results.Ok(list.Select(t =>
                new TerminalSummaryDto(
                    t.Id, t.Label, t.SiteIds, t.IsActive, t.CreatedAt, t.IsEnrolled, t.PendingTicketExpiresAt)).ToList());
        })
        .WithName("AdminListTerminals")
        .WithSummary("Liste les terminaux enrôlés (jamais leur clé).");

        group.MapPost("/terminals/{id:guid}/revoke", async (
            Guid id,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var before = await terminals.ListAsync(ct);
            var target = before.FirstOrDefault(t => t.Id == id);
            if (target is null)
                return Results.NotFound(new { error = "Terminal introuvable." });

            await terminals.RevokeAsync(id, ct);

            await RecordOnEachSiteAsync(scopeFactory, target.SiteIds,
                AdminAuditAction.TerminalRevoked, user.HostIdentifier(), id.ToString(),
                $"Terminal « {target.Label} » révoqué.", ct);
            await activity.NotifyEntityChangedAsync("terminals", ct);

            return Results.Ok(new { message = "Terminal révoqué." });
        })
        .WithName("AdminRevokeTerminal")
        .WithSummary("Révoque un terminal (clé désactivée, historique conservé).");

        // Suppression logique (archivage) : n'agit que sur un terminal déjà
        // révoqué (discipline en deux temps). Le device redevient réenrôlable
        // comme un NOUVEAU terminal (voir index unique partiel sur
        // DeviceInstanceId, NovAccesIdentityDbContext).
        group.MapPost("/terminals/{id:guid}/delete", async (
            Guid id,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
            IAdminActivityBroadcaster activity,
            CancellationToken ct) =>
        {
            var before = await terminals.ListAsync(ct);
            var target = before.FirstOrDefault(t => t.Id == id);
            if (target is null)
                return Results.NotFound(new { error = "Terminal introuvable." });

            if (target.IsActive)
                return Results.Conflict(new { error = "Révoquez le terminal avant de le supprimer." });

            await terminals.DeleteAsync(id, user.HostIdentifier(), ct);

            await RecordOnEachSiteAsync(scopeFactory, target.SiteIds,
                AdminAuditAction.TerminalDeleted, user.HostIdentifier(), id.ToString(),
                $"Terminal « {target.Label} » supprimé (archivé).", ct);
            await activity.NotifyEntityChangedAsync("terminals", ct);

            return Results.Ok(new { message = "Terminal supprimé (archivé)." });
        })
        .WithName("AdminDeleteTerminal")
        .WithSummary("Supprime (archive) un terminal déjà révoqué — device réutilisable ensuite.");

        group.MapGet("/terminals/archived", async (ITerminalDirectory terminals, CancellationToken ct) =>
        {
            var list = await terminals.ListArchivedAsync(ct);
            return Results.Ok(list.Select(t =>
                new ArchivedTerminalSummaryDto(t.Id, t.Label, t.SiteIds, t.DeletedAt, t.DeletedBy)).ToList());
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("AdminListArchivedTerminals")
        .WithSummary("Liste les terminaux supprimés (archivés), en lecture seule (SuperAdmin uniquement).");

        return group;
    }

    // Un terminal n'appartient à aucun tenant précis (il peut en servir
    // plusieurs) : on inscrit l'action dans le journal d'audit de CHAQUE site
    // concerné, un scope + une résolution de tenant par site (même schéma que
    // la création d'agent, ci-dessus).
    private static async Task RecordOnEachSiteAsync(
        IServiceScopeFactory scopeFactory, IReadOnlyList<string> siteIds,
        AdminAuditAction action, string actor, string? targetId, string detail, CancellationToken ct)
    {
        foreach (var siteId in siteIds)
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Resolve(siteId);
            var audit = scope.ServiceProvider.GetRequiredService<IAdminAuditLog>();
            await audit.RecordAsync(action, actor, targetId, detail, ct);
        }
    }

    // ---- Export d'un site (visites, journal, audit) en CSV zippé ----

    private static async Task WriteCsvEntryAsync(
        ZipArchive zip, string entryName, string header, IEnumerable<string> rows, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(header);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(row);
        }
    }

    // Échappement CSV (séparateur ';') + neutralisation de l'injection de
    // formule — même règle que DashboardEndpoints.Csv (OWASP), dupliquée ici
    // faute d'utilitaire partagé pour un si petit helper.
    private static string Csv(string value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
