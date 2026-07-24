using Microsoft.AspNetCore.Identity;
using NovAcces.Application.Abstractions;
using NovAcces.Infrastructure.Identity;
using NovAcces.Shared.Auth;
using NovAcces.Shared.Dtos;

namespace NovAcces.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        // --- Connexion (anonyme) : renvoie un JWT ---
        group.MapPost("/login", async (
            LoginRequestDto request,
            UserManager<ApplicationUser> users,
            IJwtTokenService jwt,
            CancellationToken ct) =>
        {
            // Message d'échec toujours générique : ne jamais révéler si c'est
            // l'email ou le mot de passe qui est faux (énumération de comptes).
            var invalid = Results.Json(new { error = "Identifiants invalides." }, statusCode: StatusCodes.Status401Unauthorized);

            var user = await users.FindByEmailAsync(request.Email);
            if (user is null)
                return invalid;

            if (await users.IsLockedOutAsync(user))
                return Results.Json(new { error = "Compte temporairement verrouillé." }, statusCode: StatusCodes.Status401Unauthorized);

            if (!await users.CheckPasswordAsync(user, request.Password))
            {
                await users.AccessFailedAsync(user); // incrémente le compteur de verrouillage
                return invalid;
            }

            await users.ResetAccessFailedCountAsync(user);

            var roles = await users.GetRolesAsync(user);
            var (token, expiresAt) = jwt.CreateToken(
                user.Id, user.Email!, user.DisplayName, roles, user.SiteId);

            return Results.Ok(new LoginResponseDto(token, expiresAt, user.DisplayName, roles.ToList(), user.SiteId));
        })
        .WithName("Login")
        .WithSummary("Authentifie un utilisateur du portail et renvoie un JWT.");

        // --- Création de compte (Admin uniquement) ---
        group.MapPost("/register", async (
            RegisterUserRequestDto request,
            UserManager<ApplicationUser> users,
            CancellationToken ct) =>
        {
            if (!NovAccesRoles.All.Contains(request.Role))
                return Results.BadRequest(new { error = $"Rôle invalide. Attendus : {string.Join(", ", NovAccesRoles.All)}." });

            // Tout rôle non-Admin est rattaché à un site ; Admin est global.
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

        return group;
    }
}
