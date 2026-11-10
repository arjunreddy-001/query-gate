using System.Text.Json;
using DbAccessManager.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DbAccessManager.Tests;

public class WebhookAndSmtpTests
{
    private sealed class DummyHostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string WebRootPath { get; set; } = "/tmp";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Slack")]
    [InlineData("Teams")]
    [InlineData("ZohoCliq")]
    [InlineData("Discord")]
    [InlineData("Generic")]
    public void WebhookPayload_Constructs_Valid_Json_For_All_Platforms(string platform)
    {
        var payload = WebhookService.BuildPayload(platform, "Requests", "Access requested", "Alice requested SELECT on Orders", "/admin/approvals");
        var json = JsonSerializer.Serialize(payload);
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void WebhookService_Platform_And_Filter_Labels_Are_Defined()
    {
        foreach (var p in WebhookService.Platforms)
        {
            var label = WebhookService.PlatformLabel(p);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }

        foreach (var f in WebhookService.EventFilters)
        {
            var label = WebhookService.EventFilterLabel(f);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }

    [Fact]
    public void CryptoService_Encrypt_And_Decrypt_RoundTrips_SmtpPassword()
    {
        var rawKey = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = rawKey
            })
            .Build();

        var crypto = new CryptoService(config, new DummyHostEnv());
        var password = "P@ssw0rd_With_Special_Chars!#$123";

        var encrypted = crypto.Encrypt(password);
        Assert.StartsWith("v1:", encrypted);
        Assert.NotEqual(password, encrypted);

        var decrypted = crypto.Decrypt(encrypted);
        Assert.Equal(password, decrypted);
    }

    [Fact]
    public void CryptoService_AutoGenerates_And_Persists_Key_In_DataProtection_Directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "qg-test-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataProtection:KeyPath"] = tempDir
                })
                .Build();

            var env = new DummyHostEnv { EnvironmentName = "Production" };
            var crypto1 = new CryptoService(config, env);

            var keyFile = Path.Combine(tempDir, "encryption.key");
            Assert.True(File.Exists(keyFile));

            var encrypted = crypto1.Encrypt("secret-value");

            // Second instance in new process/restart should read the same persisted key
            var crypto2 = new CryptoService(config, env);
            var decrypted = crypto2.Decrypt(encrypted);
            Assert.Equal("secret-value", decrypted);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TokenService_AutoGenerates_And_Persists_Key_In_DataProtection_Directory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "qg-test-jwt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataProtection:KeyPath"] = tempDir
                })
                .Build();

            var env = new DummyHostEnv { EnvironmentName = "Production" };
            var key1 = TokenService.BuildKey(config, env);

            var jwtFile = Path.Combine(tempDir, "jwt.key");
            Assert.True(File.Exists(jwtFile));

            var key2 = TokenService.BuildKey(config, env);
            Assert.Equal(key1.Key, key2.Key);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SmtpConfig_Record_Handles_Configured_State_Correctly()
    {
        var configured = new SmtpConfig("smtp.example.com", 587, true, "user@example.com", "secret", "noreply@example.com", true, true);
        Assert.True(configured.IsConfigured);
        Assert.True(configured.HasSavedPassword);
        Assert.Equal(587, configured.Port);
        Assert.True(configured.EnableSsl);

        var unconfigured = new SmtpConfig("", 587, true, null, null, "noreply@localhost", false, false);
        Assert.False(unconfigured.IsConfigured);
        Assert.False(unconfigured.HasSavedPassword);
    }
}
