using System.Security.Claims;

namespace NovAcces.Shared.Auth;

/// <summary>
/// Matrice de capacités fonctionnelles. Les policies protègent l'accès aux
/// routes ; cette matrice centralise les décisions métier qui dépendent du rôle
/// et sont nécessaires à l'intérieur d'une route (propriété, portée globale,
/// création de rôle sensible).
/// </summary>
public static class NovAccesAuthorizationMatrix
{
    public static bool IsSuperAdmin(ClaimsPrincipal user) =>
        user.IsInRole(NovAccesRoles.SuperAdmin);

    public static bool IsGlobalOperator(ClaimsPrincipal user) =>
        user.IsInRole(NovAccesRoles.Admin) || IsSuperAdmin(user);

    public static bool CanViewAnyVisit(ClaimsPrincipal user) =>
        user.IsInRole(NovAccesRoles.Surete) || IsGlobalOperator(user);

    public static bool CanRevokeAnyVisit(ClaimsPrincipal user) =>
        user.IsInRole(NovAccesRoles.Surete) || IsGlobalOperator(user);

    public static bool CanCreateElevatedAccount(ClaimsPrincipal user) =>
        IsSuperAdmin(user);

    public static bool CanViewAllUsers(ClaimsPrincipal user) =>
        IsSuperAdmin(user);

    /// <summary>
    /// Vérifie la hiérarchie de gestion d'un compte (désactivation, édition,
    /// réinitialisation de mot de passe) : un Admin peut gérer les comptes
    /// ordinaires ; seul un SuperAdmin peut toucher un compte Admin ou
    /// SuperAdmin.
    /// </summary>
    public static bool CanManageAccount(
        ClaimsPrincipal caller, IEnumerable<string> targetRoles)
    {
        if (!IsGlobalOperator(caller))
            return false;

        var roles = targetRoles.ToArray();
        return !roles.Contains(NovAccesRoles.Admin, StringComparer.Ordinal)
            && !roles.Contains(NovAccesRoles.SuperAdmin, StringComparer.Ordinal)
            || IsSuperAdmin(caller);
    }
}