using DbAccessManager.Components.Account;
using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class RedirectAndSettingsTests
{
    [Theory]
    [InlineData("dashboard", "http://localhost:5175/", true)]
    [InlineData("/query", "http://localhost:5175/", true)]
    [InlineData("/admin/users?page=1", "http://localhost:5175/", true)]
    [InlineData("http://localhost:5175/dashboard", "http://localhost:5175/", true)]
    [InlineData("https://evil.com", "http://localhost:5175/", false)]
    [InlineData("//evil.com", "http://localhost:5175/", false)]
    [InlineData("/\\evil.com", "http://localhost:5175/", false)]
    [InlineData("javascript:alert(1)", "http://localhost:5175/", false)]
    [InlineData("", "http://localhost:5175/", false)]
    [InlineData(null, "http://localhost:5175/", false)]
    [InlineData("http://attacker.com/dashboard", "http://localhost:5175/", false)]
    public void IsLocalUrl_ValidatesCorrectly(string? url, string baseUri, bool expected)
    {
        var result = IdentityRedirectManager.IsLocalUrl(url!, baseUri);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SettingsDefinitions_ContainsRequiredCoreKeys()
    {
        Assert.True(SettingsService.Definitions.ContainsKey("DefaultTimeoutSeconds"));
        Assert.True(SettingsService.Definitions.ContainsKey("MaxTimeoutSeconds"));
        Assert.True(SettingsService.Definitions.ContainsKey("DefaultMaxRows"));
        Assert.False(SettingsService.Definitions.ContainsKey("NonExistentCustomSettingKey"));
    }
}
