using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
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
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Matricule))
            {
                var siteId = http.Headers["X-Site-Id"].ToString();
                if (!CurrentTenant.IsValidSiteId(siteId))
                    return Results.BadRequest(new { error = "X-Site-Id requis et invalide pour la connexion agent." });

                tenant.Resolve(siteId);
                var agent = await agents.VerifyAsync(request.Matricule, request.EffectivePassword, ct);
                if (agent is null)
                    return InvalidCredentials();

                var (agentToken, agentExpiresAt) = jwt.CreateAgentToken(agent.Matricule, agent.DisplayName, tenant.SiteId);
                var agentRefresh = await refresh.IssueAsync("agent", $"{tenant.SiteId}:{agent.Matricule}", agent.DisplayName, tenant.SiteId, ct);
                return Results.Ok(new AgentLoginResponseDto(agentToken, agentRefresh.Token, SecondsUntil(agentExpiresAt), new AgentLoginIdentityDto(agent.Matricule, agent.DisplayName)));
            }

            var user = await AuthenticatePasswordAsync(users, request.Email, request.EffectivePassword);
            if (user is null)
                return InvalidCredentials();

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

            // À défaut d'un TOTP valide, on tente un code de récupération (usage unique).
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

        // --- Création de compte (Admin uniquement, sauf Admin/SuperAdmin) ---
        group.MapPost("/register", async (
            RegisterUserRequestDto request,
            System.Security.Claims.ClaimsPrincipal caller,
            UserManager<ApplicationUser> users) =>
        {
            if (!NovAccesRoles.All.Contains(request.Role))
                return Results.BadRequest(new { error = $"Rôle invalide. Attendus : {string.Join(", ", NovAccesRoles.All)}." });

            // Un compte Admin ou SuperAdmin donne un accès global : seul un
            // SuperAdmin peut en créer un (jamais un Admin simple, même pour
            // lui-même) — protection contre l'auto-escalade côté client.
            var isElevatedRole = request.Role is NovAccesRoles.Admin or NovAccesRoles.SuperAdmin;
            if (isElevatedRole && !caller.IsInRole(NovAccesRoles.SuperAdmin))
                return Results.Json(
                    new { error = "Seul le SuperAdmin peut créer un compte Admin ou SuperAdmin." },
                    statusCode: StatusCodes.Status403Forbidden);

            var isGlobal = isElevatedRole;
            if (!isGlobal && string.IsNullOrWhiteSpace(request.SiteId))
                return Results.BadRequest(new { error = "SiteId requis pour un rôle rattaché à un site." });

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName,
                SiteId = isGlobal ? null : request.SiteId,
                EmailConfirmed = true,
            };

            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
                return Results.BadRequest(new { error = "Création refusée.", details = created.Errors.Select(e => e.Description) });

            await users.AddToRoleAsync(user, request.Role);
            // SuperAdmin hérite AUSSI du rôle Admin : toutes les policies existantes
            // qui exigent Admin restent satisfaites sans devoir toucher à chacune.
            if (request.Role == NovAccesRoles.SuperAdmin)
                await users.AddToRoleAsync(user, NovAccesRoles.Admin);

            return Results.Ok(new { user.Id, user.Email, user.DisplayName, request.Role, user.SiteId });
        })
        .RequireAuthorization(NovAccesRoles.Admin)
        .WithName("RegisterUser")
        .WithSummary("Crée un compte utilisateur (réservé à l'Admin ; Admin/SuperAdmin réservés au SuperAdmin).");

        group.MapPost("/refresh", async (
            RefreshTokenRequestDto request,
            IRefreshTokenService refresh,
            UserManager<ApplicationUser> users,
            IJwtTokenService jwt,
            CancellationToken ct) =>
        {
            var subject = await refresh.RotateAsync(request.RefreshToken, ct);
            if (subject is null)
                return Results.Json(new { error = "Refresh token invalide, expiré ou révoqué." }, statusCode: StatusCodes.Status401Unauthorized);

            if (subject.SubjectType == "user")
            {
                var user = await users.FindByIdAsync(subject.SubjectId);
                if (user is null) return InvalidCredentials();
                var roles = await users.GetRolesAsync(user);
                var (token, expiresAt) = jwt.CreateToken(user.Id, user.Email!, user.DisplayName, roles, user.SiteId);
                var next = await refresh.IssueAsync("user", user.Id.ToString(), user.DisplayName, user.SiteId, ct);
                return Results.Ok(new LoginResponseDto(token, expiresAt, user.DisplayName, roles.ToList(), user.SiteId, next.Token, SecondsUntil(expiresAt)));
            }

            if (subject.SubjectType == "agent" && !string.IsNullOrWhiteSpace(subject.SiteId))
            {
                var separator = subject.SubjectId.IndexOf(':');
                var matricule = separator >= 0 ? subject.SubjectId[(separator + 1)..] : subject.SubjectId;
                var name = subject.DisplayName ?? matricule;
                var (token, expiresAt) = jwt.CreateAgentToken(matricule, name, subject.SiteId);
                var next = await refresh.IssueAsync("agent", subject.SubjectId, name, subject.SiteId, ct);
                return Results.Ok(new AgentLoginResponseDto(token, next.Token, SecondsUntil(expiresAt), new AgentLoginIdentityDto(matricule, name)));
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
            var deleted = await users.DeleteAsync(user);
            if (!deleted.Succeeded)
                return Results.BadRequest(new { error = "Suppression du compte refusée.", details = deleted.Errors.Select(e => e.Description) });

            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("SelfDeleteAccount")
        .WithSummary("Supprime uniquement le compte de l'utilisateur authentifié (self-delete)." );
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

        if (await users.IsLockedOutAsync(user))
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
