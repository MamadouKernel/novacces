using System.Security.Claims;
using NovAcces.Shared.Auth;
using Xunit;

namespace NovAcces.UnitTests.Security;

public sealed class NovAccesAuthorizationMatrixTests
{
    private static ClaimsPrincipal UserWithRole(string role)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void Admin_CanManage_AnotherAdminAccount()
    {
        var admin = UserWithRole(NovAccesRoles.Admin);

        Assert.True(NovAccesAuthorizationMatrix.CanManageAccount(admin, new[] { NovAccesRoles.Admin }));
    }

    [Fact]
    public void Admin_CanManage_OrdinaryAccounts()
    {
        var admin = UserWithRole(NovAccesRoles.Admin);

        Assert.True(NovAccesAuthorizationMatrix.CanManageAccount(admin, new[] { NovAccesRoles.Hote }));
        Assert.True(NovAccesAuthorizationMatrix.CanManageAccount(admin, new[] { NovAccesRoles.Surete }));
    }

    [Fact]
    public void Admin_CannotManage_SuperAdminAccount()
    {
        var admin = UserWithRole(NovAccesRoles.Admin);

        Assert.False(NovAccesAuthorizationMatrix.CanManageAccount(admin, new[] { NovAccesRoles.SuperAdmin }));
    }

    [Fact]
    public void SuperAdmin_CanManage_AnyAccountIncludingSuperAdmin()
    {
        var superAdmin = UserWithRole(NovAccesRoles.SuperAdmin);

        Assert.True(NovAccesAuthorizationMatrix.CanManageAccount(superAdmin, new[] { NovAccesRoles.SuperAdmin }));
        Assert.True(NovAccesAuthorizationMatrix.CanManageAccount(superAdmin, new[] { NovAccesRoles.Admin }));
        Assert.True(NovAccesAuthorizationMatrix.CanManageAccount(superAdmin, new[] { NovAccesRoles.Hote }));
    }

    [Fact]
    public void OrdinaryRole_CannotManage_AnyAccount()
    {
        var hote = UserWithRole(NovAccesRoles.Hote);

        Assert.False(NovAccesAuthorizationMatrix.CanManageAccount(hote, new[] { NovAccesRoles.Hote }));
        Assert.False(NovAccesAuthorizationMatrix.CanManageAccount(hote, new[] { NovAccesRoles.Admin }));
    }

    [Fact]
    public void Admin_CannotActOnOwnAccount()
    {
        var admin = UserWithRole(NovAccesRoles.Admin);

        Assert.False(NovAccesAuthorizationMatrix.CanActOnOwnAccount(admin));
    }

    [Fact]
    public void SuperAdmin_CanActOnOwnAccount()
    {
        var superAdmin = UserWithRole(NovAccesRoles.SuperAdmin);

        Assert.True(NovAccesAuthorizationMatrix.CanActOnOwnAccount(superAdmin));
    }
}
