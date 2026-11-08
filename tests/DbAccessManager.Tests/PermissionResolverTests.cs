using DbAccessManager.Security;

namespace DbAccessManager.Tests;

public sealed class PermissionResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PermissionResolver _resolver = new();

    [Fact]
    public void Resolve_DefaultsToDeny_WhenNoActiveRuleExists()
    {
        var result = _resolver.Resolve("SELECT", [], Now);

        Assert.False(result.IsAllowed);
        Assert.Equal(PermissionSource.Default, result.Source);
    }

    [Fact]
    public void Resolve_DenyWinsAcrossRoles()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, true, "Developer"),
            new PermissionRule("SELECT", false, false, "Restricted")
        };

        var result = _resolver.Resolve("SELECT", rules, Now);

        Assert.False(result.IsAllowed);
        Assert.Equal(PermissionSource.Role, result.Source);
        Assert.Equal(["Developer", "Restricted"], result.Roles);
    }

    [Fact]
    public void Resolve_UserAllowOverridesRoleDeny()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, false, "Restricted"),
            new PermissionRule("SELECT", true, true)
        };

        var result = _resolver.Resolve("SELECT", rules, Now);

        Assert.True(result.IsAllowed);
        Assert.Equal(PermissionSource.User, result.Source);
    }

    [Fact]
    public void Resolve_UserDenyWinsOverUserAllow()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", true, true),
            new PermissionRule("SELECT", true, false)
        };

        var result = _resolver.Resolve("SELECT", rules, Now);

        Assert.False(result.IsAllowed);
        Assert.Equal(PermissionSource.User, result.Source);
    }

    [Fact]
    public void Resolve_IgnoresFutureAndExpiredRules()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", true, true, StartsAt: Now.AddSeconds(1)),
            new PermissionRule("SELECT", false, true, "Expired", EndsAt: Now)
        };

        var result = _resolver.Resolve("SELECT", rules, Now);

        Assert.False(result.IsAllowed);
        Assert.Equal(PermissionSource.Default, result.Source);
    }

    [Fact]
    public void Resolve_MatchesPermissionCodeWithoutCaseSensitivity()
    {
        var result = _resolver.Resolve(
            "select",
            [new PermissionRule("SELECT", true, true)],
            Now);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Perm_UnmaskLabel_IsViewUnmaskedData()
    {
        Assert.Equal("View unmasked data", Perm.Label(Perm.Unmask));
    }

    [Fact]
    public void Perm_AllCatalogCodesHaveValidDescriptions()
    {
        foreach (var code in Perm.Catalog)
        {
            var desc = Perm.Description(code);
            Assert.False(string.IsNullOrWhiteSpace(desc));
        }
    }
}
