using System.Text;
using Microsoft.AspNetCore.Identity;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class AuthEndpoints
{
    private const string AuthenticatorIssuer = "NovAcces";

    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        // --- Connexion, étape 1 (anonyme) ---
        group.MapPost("/login", async (
            LoginRequestDto request,
            UserManager<ApplicationUser> users,
            IJwtTokenService jwt) =>
        {
            var user = await AuthenticatePasswordAsync(users, request.Email, request.Password);
            if (user is null)
                return InvalidCredentials();

            // 2FA activé : on ne délivre AUCUN jeton ici ; le client doit fournir
            // le second facteur via /login/2fa.
            if (await users.GetTwoFactorEnabledAsync(user))
                return Results.Ok(new TwoFactorRequiredDto());

            return Results.Ok(await BuildLoginResponseAsync(users, jwt, user));
        })
        .WithName("Login")
        .WithSummary("Authentifie un utilisateur ; signale si un second facteur est requis.");

        // --- Connexion, étape 2 (second facteur : TOTP ou code de récupération) ---
        group.MapPost("/login/2fa", async (
            TwoFactorLoginRequestDto request,
            UserManager<ApplicationUser> users,
            IJwtTokenService jwt) =>
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
            return Results.Ok(await BuildLoginResponseAsync(users, jwt, user));
        })
        .WithName("LoginTwoFactor")
        .WithSummary("Valide le second facteur (TOTP ou code de récupération) et délivre le JWT.");

        // --- Création de compte (Admin uniquement) ---
        group.MapPost("/register", async (
            RegisterUserRequestDto request,
            UserManager<ApplicationUser> users) =>
        {
            if (!NovAccesRoles.All.Contains(request.Role))
                return Results.BadRequest(new { error = $"Rôle invalide. Attendus : {string.Join(", ", NovAccesRoles.All)}." });

            var isGlobal = request.Role == NovAccesRoles.Admin;
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
            return Results.Ok(new { user.Id, user.Email, user.DisplayName, request.Role, user.SiteId });
        })
        .RequireAuthorization(NovAccesRoles.Admin)
        .WithName("RegisterUser")
        .WithSummary("Crée un compte utilisateur (réservé à l'Admin).");

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

    private static async Task<ApplicationUser?> AuthenticatePasswordAsync(
        UserManager<ApplicationUser> users, string email, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
            return null;

        if (await users.IsLockedOutAsync(user))
            return null;

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return null;
        }

        return user;
    }

    private static async Task<LoginResponseDto> BuildLoginResponseAsync(
        UserManager<ApplicationUser> users, IJwtTokenService jwt, ApplicationUser user)
    {
        await users.ResetAccessFailedCountAsync(user);
        var roles = await users.GetRolesAsync(user);
        var (token, expiresAt) = jwt.CreateToken(user.Id, user.Email!, user.DisplayName, roles, user.SiteId);
        return new LoginResponseDto(token, expiresAt, user.DisplayName, roles.ToList(), user.SiteId);
    }

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
