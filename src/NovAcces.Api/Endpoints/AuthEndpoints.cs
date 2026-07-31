using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NovAcces.Api.Auth;
using NovAcces.Application.Abstractions;
using NovAcces.Domain.Enums;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;
using NovAcces.Infrastructure.Persistence.Tenancy;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class AuthEndpoints
{
    private const string AuthenticatorIssuer = "NovAcces";

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        // --- Connexion, étape 1 (Web ou agent mobile) ---
        group.MapPost("/login", async (
            LoginRequestDto request,
            UserManager<ApplicationUser> users,
            IAgentDirectory agents,
            IJwtTokenService jwt,
            IRefreshTokenService refresh,
            CurrentTenant tenant,
            HttpRequest http,
            ClaimsPrincipal principal,
            IOptions<AuthenticationSecurityOptions> security,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Matricule))
            {
                if (!Guid.TryParse(principal.FindFirstValue(NovAccesClaimTypes.TerminalId), out var terminalId))
                    return Results.Json(new { error = "Un terminal enrôlé est requis pour la connexion agent." },
                        statusCode: StatusCodes.Status401Unauthorized);

                var siteId = http.Headers["X-Site-Id"].ToString();
                if (!CurrentTenant.IsValidSiteId(siteId))
                    return Results.BadRequest(new { error = "X-Site-Id requis et invalide pour la connexion agent." });

                tenant.Resolve(siteId);
                var agent = await agents.VerifyAsync(request.Matricule, request.EffectivePassword, ct);
                if (agent is null)
                    return InvalidCredentials();

                var (agentToken, agentExpiresAt) = jwt.CreateAgentToken(agent.Matricule, agent.DisplayName, tenant.SiteId, terminalId);
                var agentRefresh = await refresh.IssueAsync("agent", $"{tenant.SiteId}:{agent.Matricule}:{terminalId:D}", agent.DisplayName, tenant.SiteId, ct);
                return Results.Ok(new AgentLoginResponseDto(agentToken, agentRefresh.Token, SecondsUntil(agentExpiresAt), new AgentLoginIdentityDto(agent.Matricule, agent.DisplayName)));
            }

            var user = await AuthenticatePasswordAsync(users, request.Email, request.EffectivePassword);
            if (user is null || user.IsDeactivated)
                return InvalidCredentials();

            var roles = await users.GetRolesAsync(user);
            if (security.Value.RequireTwoFactorForPrivileged
                && IsPrivilegedRole(roles)
                && !await users.GetTwoFactorEnabledAsync(user))
                return Results.Ok(new TwoFactorEnrollmentRequiredDto());

            if (await users.GetTwoFactorEnabledAsync(user))
                return Results.Ok(new TwoFactorRequiredDto());

            return Results.Ok(await BuildLoginResponseAsync(users, jwt, refresh, user, ct));
        })
        .WithName("Login")
        .WithSummary("Authentifie un utilisateur ; signale si un second facteur est requis.");

        // --- Connexion, étape 2 (second facteur : TOTP ou code de récupération) ---
        group.MapPost("/login/2fa", async (
            TwoFactorLoginRequestDto request,
            UserManager<ApplicationUser> users,
            IJwtTokenService jwt,
            IRefreshTokenService refresh,
            CancellationToken ct) =>
        {
            var user = await AuthenticatePasswordAsync(users, request.Email, request.Password);
            if (user is null || !await users.GetTwoFactorEnabledAsync(user))
                return InvalidCredentials();

            var code = (request.Code ?? string.Empty).Replace(" ", string.Empty);
            var totpValid = await users.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, code);
            var accepted = totpValid
                || (await users.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded;

            if (!accepted)
            {
                await users.AccessFailedAsync(user);
                return InvalidCredentials();
            }

            await users.ResetAccessFailedCountAsync(user);
            return Results.Ok(await BuildLoginResponseAsync(users, jwt, refresh, user, ct));
        })
        .WithName("LoginTwoFactor")
        .WithSummary("Valide le second facteur (TOTP ou code de récupération) et délivre le JWT.");

        // Bootstrap TOTP initial : aucun JWT privilégié n'est délivré avant activation.
        group.MapPost("/2fa/bootstrap/setup", async (
            TwoFactorBootstrapRequestDto request,
            UserManager<ApplicationUser> users,
            IOptions<AuthenticationSecurityOptions> security) =>
        {
            if (!security.Value.RequireTwoFactorForPrivileged)
                return Results.BadRequest(new { error = "L'enrôlement obligatoire du 2FA est désactivé dans cet environnement." });

            var user = await AuthenticatePasswordAsync(users, request.Email, request.Password);
            if (user is null || user.IsDeactivated)
                return InvalidCredentials();

            var roles = await users.GetRolesAsync(user);
            if (!IsPrivilegedRole(roles) || await users.GetTwoFactorEnabledAsync(user))
                return InvalidCredentials();

            var key = await users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrWhiteSpace(key))
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
            }

            return Results.Ok(new TwoFactorSetupDto(key!, BuildAuthenticatorUri(user.Email!, key!)));
        })
        .WithName("TwoFactorBootstrapSetup")
        .WithSummary("Prépare l'enrôlement TOTP initial sans délivrer de JWT.");

        group.MapPost("/2fa/bootstrap/enable", async (
            TwoFactorBootstrapEnableRequestDto request,
            UserManager<ApplicationUser> users,
            IOptions<AuthenticationSecurityOptions> security) =>
        {
            if (!security.Value.RequireTwoFactorForPrivileged)
                return Results.BadRequest(new { error = "L'enrôlement obligatoire du 2FA est désactivé dans cet environnement." });

            var user = await AuthenticatePasswordAsync(users, request.Email, request.Password);
            if (user is null || user.IsDeactivated)
                return InvalidCredentials();

            var roles = await users.GetRolesAsync(user);
            if (!IsPrivilegedRole(roles) || await users.GetTwoFactorEnabledAsync(user))
                return InvalidCredentials();

            var code = (request.Code ?? string.Empty).Replace(" ", string.Empty);
            if (!await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code))
            {
                await users.AccessFailedAsync(user);
                return InvalidCredentials();
            }

            var enabled = await users.SetTwoFactorEnabledAsync(user, true);
            if (!enabled.Succeeded)
                return Results.BadRequest(new { error = "Activation du 2FA refusée." });

            await users.ResetAccessFailedCountAsync(user);
            var recovery = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            return Results.Ok(new TwoFactorRecoveryCodesDto(recovery?.ToList() ?? new List<string>()));
        })
        .WithName("TwoFactorBootstrapEnable")
        .WithSummary("Active le TOTP initial après vérification du code.");

        // --- Création de compte (Admin uniquement, sauf Admin/SuperAdmin) ---
        group.MapPost("/register", async (
            RegisterUserRequestDto request,
            ClaimsPrincipal caller,
            UserManager<ApplicationUser> users,
            ISiteCatalog sites,
            CancellationToken ct) =>
        {
            var role = request.Role?.Trim();
            if (string.IsNullOrWhiteSpace(role) || !NovAccesRoles.All.Contains(role, StringComparer.Ordinal))
                return Results.BadRequest(new { error = $"Rôle invalide. Attendus : {string.Join(", ", NovAccesRoles.All)}." });

            var isElevatedRole = role is NovAccesRoles.Admin or NovAccesRoles.SuperAdmin;
            if (isElevatedRole && !NovAccesAuthorizationMatrix.CanCreateElevatedAccount(caller))
                return Results.Json(
                    new { error = "Seul le SuperAdmin peut créer un compte Admin ou SuperAdmin." },
                    statusCode: StatusCodes.Status403Forbidden);

            var isGlobal = isElevatedRole;
            var siteId = request.SiteId?.Trim().ToLowerInvariant();
            if (!isGlobal && !CurrentTenant.IsValidSiteId(siteId))
                return Results.BadRequest(new { error = "SiteId requis pour un rôle rattaché à un site." });

            if (!isGlobal)
            {
                var provisionedSites = await sites.GetSiteIdsAsync(ct);
                if (!provisionedSites.Contains(siteId!, StringComparer.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Le site demandé n'est pas provisionné." });
            }

            var email = request.Email?.Trim();
            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(email) || email.Length > 254
                || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160)
                return Results.BadRequest(new { error = "Email ou nom affiché invalide." });

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = displayName,
                SiteId = isGlobal ? null : siteId,
                EmailConfirmed = true,
            };

            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
                return Results.BadRequest(new { error = "Création refusée.", details = created.Errors.Select(e => e.Description) });

            var roleResult = await users.AddToRoleAsync(user, role);
            if (!roleResult.Succeeded)
                return Results.BadRequest(new { error = "Rôle non attribué.", details = roleResult.Errors.Select(e => e.Description) });

            if (role == NovAccesRoles.SuperAdmin)
                await users.AddToRoleAsync(user, NovAccesRoles.Admin);

            return Results.Ok(new { user.Id, user.Email, user.DisplayName, Role = role, user.SiteId });
        })
        .RequireAuthorization(NovAccesRoles.Admin)
        .WithName("RegisterUser")
        .WithSummary("Crée un compte utilisateur (réservé à l'Admin ; Admin/SuperAdmin réservés au SuperAdmin).");

        group.MapPost("/refresh", async (
            RefreshTokenRequestDto request,
            IRefreshTokenService refresh,
            UserManager<ApplicationUser> users,
            IJwtTokenService jwt,
            IOptions<AuthenticationSecurityOptions> security,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var subject = await refresh.RotateAsync(request.RefreshToken, ct);
            if (subject is null)
                return Results.Json(new { error = "Refresh token invalide, expiré ou révoqué." },
                    statusCode: StatusCodes.Status401Unauthorized);

            if (subject.SubjectType == "user")
            {
                var user = await users.FindByIdAsync(subject.SubjectId);
                if (user is null || user.IsDeactivated)
                return InvalidCredentials();

                var roles = await users.GetRolesAsync(user);
                if (security.Value.RequireTwoFactorForPrivileged
                    && IsPrivilegedRole(roles)
                    && !await users.GetTwoFactorEnabledAsync(user))
                    return Results.Json(new { error = "Enrôlement 2FA requis avant de renouveler la session." },
                        statusCode: StatusCodes.Status403Forbidden);

                var (token, expiresAt) = jwt.CreateToken(user.Id, user.Email!, user.DisplayName, roles, user.SiteId);
                var next = await refresh.IssueAsync("user", user.Id.ToString(), user.DisplayName, user.SiteId, ct);
                return Results.Ok(new LoginResponseDto(token, expiresAt, user.DisplayName, roles.ToList(),
                    user.SiteId, next.Token, SecondsUntil(expiresAt)));
            }

            if (subject.SubjectType == "agent" && !string.IsNullOrWhiteSpace(subject.SiteId))
            {
                var parts = subject.SubjectId.Split(':', 3, StringSplitOptions.None);
                if (parts.Length != 3
                    || !Guid.TryParse(parts[2], out var terminalId)
                    || !Guid.TryParse(principal.FindFirstValue(NovAccesClaimTypes.TerminalId), out var presentedTerminalId)
                    || terminalId != presentedTerminalId)
                    return Results.Json(new { error = "Un terminal enrôlé correspondant est requis pour renouveler la session agent." },
                        statusCode: StatusCodes.Status401Unauthorized);
                var matricule = parts[1];
                var name = subject.DisplayName ?? matricule;
                var (token, expiresAt) = jwt.CreateAgentToken(matricule, name, subject.SiteId, terminalId);
                var next = await refresh.IssueAsync("agent", subject.SubjectId, name, subject.SiteId, ct);
                return Results.Ok(new AgentLoginResponseDto(token, next.Token, SecondsUntil(expiresAt),
                    new AgentLoginIdentityDto(matricule, name)));
            }

            return InvalidCredentials();
        })
        .WithName("Refresh")
        .WithSummary("Fait tourner un refresh token et délivre une nouvelle session.");

        group.MapPost("/logout", async (RefreshTokenRequestDto request, IRefreshTokenService refresh, CancellationToken ct) =>
        {
            await refresh.RevokeAsync(request.RefreshToken, ct);
            return Results.NoContent();
        })
        .WithName("Logout")
        .WithSummary("Révoque le refresh token de la session.");

        // --- Suppression de compte : self-delete uniquement ---
        group.MapDelete("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            IRefreshTokenService refresh,
            CurrentTenant tenant,
            IAdminAuditLog audit,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            var actor = user.Id.ToString();
            // Le compte ne peut supprimer que son propre compte : aucun id cible
            // n'est accepté par cette route. L'action est journalisée avant la
            // suppression afin de conserver une preuve même si Identity échoue.
            if (!string.IsNullOrWhiteSpace(user.SiteId))
            {
                tenant.Resolve(user.SiteId);
                await audit.RecordAsync(
                    AdminAuditAction.AccountSelfDeleted, actor, actor,
                    "Demande de suppression volontaire du compte par son titulaire.", ct);
            }
            else
            {
                loggerFactory.CreateLogger("NovAcces.Auth")
                    .LogWarning("Self-delete demandé par le compte global {Actor}.", actor);
            }

            await refresh.RevokeAllForSubjectAsync("user", actor, ct);
            user.Deactivate(DateTimeOffset.UtcNow);
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
                return Results.BadRequest(new { error = "Désactivation du compte refusée.", details = updated.Errors.Select(e => e.Description) });

            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("SelfDeleteAccount")
        .WithSummary("Désactive logiquement le compte de l'utilisateur authentifié (self-delete)." );
        // --- Profil : modifier son nom affiché (authentifié) ---
        group.MapPost("/me/display-name", async (
            UpdateDisplayNameRequestDto request,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            var name = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "Nom affiché requis." });

            user.DisplayName = name;
            var result = await users.UpdateAsync(user);
            return result.Succeeded
                ? Results.Ok(new { user.DisplayName })
                : Results.BadRequest(new { error = "Mise à jour refusée." });
        })
        .RequireAuthorization()
        .WithName("UpdateDisplayName")
        .WithSummary("Modifie le nom affiché de l'utilisateur connecté.");

        // --- Profil : changer son mot de passe (authentifié, ancien requis) ---
        group.MapPost("/me/password", async (
            ChangePasswordRequestDto request,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            var result = await users.ChangePasswordAsync(
                user, request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty);

            return result.Succeeded
                ? Results.Ok(new { message = "Mot de passe modifié." })
                : Results.BadRequest(new { error = "Changement refusé.", details = result.Errors.Select(e => e.Description) });
        })
        .RequireAuthorization()
        .WithName("ChangePassword")
        .WithSummary("Change le mot de passe de l'utilisateur connecté (ancien requis).");

        // --- 2FA : enrôlement (authentifié) — renvoie la clé + l'URI otpauth ---
        group.MapPost("/2fa/setup", async (
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            // Durcissement : ne JAMAIS ré-exposer le secret TOTP quand le 2FA est
            // déjà actif (une session détournée pourrait cloner l'authentificateur).
            // Pour ré-enrôler, il faut d'abord désactiver le 2FA (mot de passe requis).
            if (await users.GetTwoFactorEnabledAsync(user))
                return Results.BadRequest(new { error = "2FA déjà activé — désactivez-le d'abord pour ré-enrôler." });

            // (Re)génère une clé d'authentificateur tant que le 2FA n'est pas activé.
            var key = await users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
            }

            var uri = BuildAuthenticatorUri(user.Email!, key!);
            return Results.Ok(new TwoFactorSetupDto(key!, uri));
        })
        .RequireAuthorization()
        .WithName("TwoFactorSetup")
        .WithSummary("Prépare l'enrôlement TOTP (clé + URI otpauth à scanner).");

        // --- 2FA : activation (authentifié) — valide un code et renvoie les codes de récupération ---
        group.MapPost("/2fa/enable", async (
            EnableTwoFactorRequestDto request,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            var code = (request.Code ?? string.Empty).Replace(" ", string.Empty);
            var valid = await users.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, code);
            if (!valid)
                return Results.BadRequest(new { error = "Code invalide — vérifiez l'heure de l'appareil et réessayez." });

            await users.SetTwoFactorEnabledAsync(user, true);
            var recovery = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);

            return Results.Ok(new TwoFactorRecoveryCodesDto(recovery?.ToList() ?? new List<string>()));
        })
        .RequireAuthorization()
        .WithName("TwoFactorEnable")
        .WithSummary("Active le 2FA après validation d'un code TOTP ; renvoie les codes de récupération.");

        // --- 2FA : désactivation (authentifié, mot de passe requis) ---
        group.MapPost("/2fa/disable", async (
            DisableTwoFactorRequestDto request,
            System.Security.Claims.ClaimsPrincipal principal,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();

            // Ré-authentification par mot de passe pour une action sensible.
            if (!await users.CheckPasswordAsync(user, request.Password))
                return Results.BadRequest(new { error = "Mot de passe invalide." });

            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user); // invalide l'ancienne clé
            return Results.Ok(new { message = "2FA désactivé." });
        })
        .RequireAuthorization()
        .WithName("TwoFactorDisable")
        .WithSummary("Désactive le 2FA (mot de passe requis).");

        return group;
    }

    // ---- Aides privées ----

    // Leurre à temps constant contre l'énumération de comptes par canal
    // temporel : même quand l'email est inconnu, on effectue le travail de
    // hachage d'un mot de passe pour que le temps de réponse ne trahisse pas
    // l'existence du compte.
    private static readonly PasswordHasher<ApplicationUser> DummyHasher = new();
    private static readonly string DummyHash =
        DummyHasher.HashPassword(new ApplicationUser(), "leurre-" + Guid.NewGuid().ToString("N"));

    private static async Task<ApplicationUser?> AuthenticatePasswordAsync(
        UserManager<ApplicationUser> users, string? email, string? password)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            DummyHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash, password ?? string.Empty);
            return null;
        }
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            // Égalise le temps de réponse avec le cas d'un compte existant.
            DummyHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash, password ?? string.Empty);
            return null;
        }

        if (user.IsDeactivated || await users.IsLockedOutAsync(user))
            return null;

        if (!await users.CheckPasswordAsync(user, password ?? string.Empty))
        {
            await users.AccessFailedAsync(user);
            return null;
        }

        return user;
    }

    private static async Task<LoginResponseDto> BuildLoginResponseAsync(
        UserManager<ApplicationUser> users, IJwtTokenService jwt, IRefreshTokenService refresh,
        ApplicationUser user, CancellationToken ct)
    {
        await users.ResetAccessFailedCountAsync(user);
        var roles = await users.GetRolesAsync(user);
        var (token, expiresAt) = jwt.CreateToken(user.Id, user.Email!, user.DisplayName, roles, user.SiteId);
        var refreshToken = await refresh.IssueAsync("user", user.Id.ToString(), user.DisplayName, user.SiteId, ct);
        return new LoginResponseDto(token, expiresAt, user.DisplayName, roles.ToList(), user.SiteId, refreshToken.Token, SecondsUntil(expiresAt));
    }

    private static bool IsPrivilegedRole(IEnumerable<string> roles) =>
        roles.Contains(NovAccesRoles.Admin, StringComparer.Ordinal)
        || roles.Contains(NovAccesRoles.SuperAdmin, StringComparer.Ordinal)
        || roles.Contains(NovAccesRoles.Surete, StringComparer.Ordinal);

    private static int SecondsUntil(DateTimeOffset expiresAt) =>
        Math.Max(0, (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalSeconds));

    // Message d'échec toujours générique (anti-énumération de comptes).
    private static IResult InvalidCredentials() =>
        Results.Json(new { error = "Identifiants invalides." }, statusCode: StatusCodes.Status401Unauthorized);

    private static string BuildAuthenticatorUri(string email, string unformattedKey)
    {
        var issuer = Uri.EscapeDataString(AuthenticatorIssuer);
        var account = Uri.EscapeDataString(email);
        return new StringBuilder("otpauth://totp/")
            .Append(issuer).Append(':').Append(account)
            .Append("?secret=").Append(unformattedKey)
            .Append("&issuer=").Append(issuer)
            .Append("&digits=6")
            .ToString();
    }
}
