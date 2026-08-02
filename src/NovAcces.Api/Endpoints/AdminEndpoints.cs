using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
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

        group.MapGet("/overview", async (ISiteOverviewService overview, CancellationToken ct) =>
        {
            var sites = await overview.GetAsync(ct);
            var dto = sites.Select(s => new AdminSiteOverviewDto(
                s.SiteId, s.OnSite, s.ScansToday,
                s.TerminalsEnrolled, s.TerminalsActive, s.DegradedScansToday)).ToList();
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
                result.BySite.Select(s => new SiteActivityTotalDto(s.SiteId, s.ScansTotal)).ToList());
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

            return Results.Ok(new
            {
                message = "Compte désactivé. Les données historiques sont conservées.",
                userId = target.Id,
                target.IsDeactivated
            });
        })
        .WithName("AdminDeactivateUser")
        .WithSummary("Désactive logiquement un compte et révoque ses sessions.");

        // Réactivation : réservée au SuperAdmin (pas la même hiérarchie que la
        // désactivation/édition — un Admin ne peut réactiver personne, même un
        // compte qu'il aurait lui-même désactivé).
        group.MapPost("/users/{id:guid}/reactivate", async (
            Guid id,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            CancellationToken ct) =>
        {
            var actor = await users.GetUserAsync(caller);
            if (actor is null)
                return Results.Unauthorized();

            var target = await users.FindByIdAsync(id.ToString());
            if (target is null)
                return Results.NotFound(new { error = "Compte introuvable." });

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

            return Results.Ok(new { message = "Compte réactivé.", userId = target.Id });
        })
        .RequireAuthorization(NovAccesRoles.SuperAdmin)
        .WithName("AdminReactivateUser")
        .WithSummary("Réactive un compte désactivé (SuperAdmin uniquement).");

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
            CancellationToken ct) =>
        {
            var role = request.Role?.Trim();
            if (string.IsNullOrWhiteSpace(role) || !NovAccesRoles.All.Contains(role, StringComparer.Ordinal))
                return Results.BadRequest(new { error = $"Rôle invalide. Attendus : {string.Join(", ", NovAccesRoles.All)}." });

            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160)
                return Results.BadRequest(new { error = "Nom affiché invalide." });

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

            var isElevatedRole = role is NovAccesRoles.Admin or NovAccesRoles.SuperAdmin;
            if (isElevatedRole && !NovAccesAuthorizationMatrix.CanCreateElevatedAccount(caller))
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
                    target.Id.ToString(), $"Compte modifié : nom « {displayName} », rôle « {role} », site « {siteId ?? "—"} ».", ct);
            }

            return Results.Ok(new { userId = target.Id, target.DisplayName, Role = role, target.SiteId });
        })
        .WithName("AdminUpdateUser")
        .WithSummary("Modifie le nom, le rôle et le site d'un compte.");

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

        // Provisionnement d'un site depuis la console : désormais possible en HTTP
        // car protégé par le rôle Admin (auth en place). Le service reste aussi
        // disponible en CLI pour l'exploitation (dotnet run -- provision-site).
        group.MapPost("/sites", async (
            ProvisionSiteRequestDto request,
            ITenantProvisioningService provisioning,
            ISiteCatalog sites,
            CancellationToken ct) =>
        {
            var siteId = request.SiteId?.Trim().ToLowerInvariant();
            if (!CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "Identifiant de site invalide (a-z, 0-9, _ ; max 40)." });

            await provisioning.ProvisionAsync(siteId!, ct);

            // Le catalogue met l'existence des sites en cache pour ne pas
            // interroger la base à chaque requête : sans invalidation, le site
            // qu'on vient de créer resterait « inconnu » le temps du TTL.
            sites.Invalidate();

            return Results.Ok(new { message = $"Site '{siteId}' provisionné." });
        })
        .WithName("AdminProvisionSite")
        .WithSummary("Provisionne un nouveau site (schéma + modèle + journal append-only).");

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

            var deactivated = await agents.DeactivateAsync(matricule, ct);
            if (!deactivated)
                return Results.NotFound(new { error = $"Agent {matricule} introuvable sur le site {siteId}." });

            await audit.RecordAsync(AdminAuditAction.AgentDeactivated, user.HostIdentifier(), matricule,
                $"Désactivation de l'agent {matricule} sur le site {siteId}.", ct);

            return Results.Ok(new { message = $"Agent {matricule} désactivé sur le site {siteId}." });
        })
        .WithName("AdminDeactivateAgent")
        .WithSummary("Désactive un agent sur un site (départ ou réaffectation vers un autre site).");

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
            var qrPayload = $"novacces://enroll?api={Uri.EscapeDataString(publicBaseUrl)}&ticket={Uri.EscapeDataString(ticket.Ticket)}&terminal={ticket.TerminalId:D}&expires={ticket.ExpiresAt.ToUnixTimeSeconds()}";

            await RecordOnEachSiteAsync(scopeFactory, ticket.SiteIds,
                AdminAuditAction.EnrollmentTicketCreated, user.HostIdentifier(), ticket.TerminalId.ToString(),
                $"Ticket QR temporaire créé pour le terminal « {ticket.Label} » (expiration {ticket.ExpiresAt:O}).", ct);

            return Results.Ok(new EnrollmentTicketResponseDto(
                ticket.TerminalId, ticket.Label, ticket.SiteIds, qrPayload, ticket.ExpiresAt));
        })
        .RequireRateLimiting("sensitive")
        .WithName("CreateTerminalEnrollmentTicket")
        .WithSummary("Génère un QR d'enrôlement temporaire, à usage unique.");

        group.MapGet("/terminals", async (ITerminalDirectory terminals, CancellationToken ct) =>
        {
            var list = await terminals.ListAsync(ct);
            return Results.Ok(list.Select(t =>
                new TerminalSummaryDto(t.Id, t.Label, t.SiteIds, t.IsActive, t.CreatedAt, t.IsEnrolled)).ToList());
        })
        .WithName("AdminListTerminals")
        .WithSummary("Liste les terminaux enrôlés (jamais leur clé).");

        group.MapPost("/terminals/{id:guid}/revoke", async (
            Guid id,
            ITerminalDirectory terminals,
            IServiceScopeFactory scopeFactory,
            ClaimsPrincipal user,
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

            return Results.Ok(new { message = "Terminal révoqué." });
        })
        .WithName("AdminRevokeTerminal")
        .WithSummary("Révoque un terminal (clé désactivée, historique conservé).");

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
}
