using System.Security.Cryptography;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class CryptoAndSettingsTests
{
    private static CryptoService CreateCryptoService(byte[]? keyBytes = null)
    {
        keyBytes ??= new byte[32];
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Encryption:Key"] = Convert.ToBase64String(keyBytes)
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        return new CryptoService(config);
    }

    private static (SettingsService Service, ApplicationDbContext Db, CryptoService Crypto, IMemoryCache Cache) CreateSettingsService(string dbName)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new ApplicationDbContext(options);
        var factory = new SimpleDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantAccessor = new TenantContextAccessor();
        var service = new SettingsService(factory, cache, tenantAccessor);
        var crypto = CreateCryptoService();
        return (service, db, crypto, cache);
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    [Fact]
    public void CryptoService_Constructor_ThrowsOnInvalidKeyLength()
    {
        var invalidKey = Convert.ToBase64String(new byte[16]); // 16 bytes instead of 32
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:Key"] = invalidKey
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new CryptoService(config));
    }

    [Fact]
    public void CryptoService_EncryptDecrypt_RoundtripSucceeds()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var crypto = CreateCryptoService(key);

        const string plaintext = "SensitiveConnectionString;Password=Secret123!";
        var cipher = crypto.Encrypt(plaintext);

        Assert.StartsWith("v1:", cipher);
        var decrypted = crypto.Decrypt(cipher);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void CryptoService_Decrypt_ThrowsOnCorruptedCiphertextOrTag()
    {
        var crypto = CreateCryptoService();
        var cipher = crypto.Encrypt("ValidSecret");

        // Mutate the ciphertext payload
        var rawBytes = Convert.FromBase64String(cipher[3..]);
        rawBytes[^1] ^= 0xFF; // Flip bits in ciphertext
        var corrupted = "v1:" + Convert.ToBase64String(rawBytes);

        Assert.ThrowsAny<CryptographicException>(() => crypto.Decrypt(corrupted));
    }

    [Fact]
    public void CryptoService_Decrypt_ThrowsOnUnknownPayloadFormat()
    {
        var crypto = CreateCryptoService();
        Assert.Throws<CryptographicException>(() => crypto.Decrypt("v2:SomeInvalidPayload"));
    }

    [Fact]
    public void CryptoService_Sha256_ComputesDeterministicHexDigest()
    {
        var hash1 = CryptoService.Sha256("test-input-string");
        var hash2 = CryptoService.Sha256("test-input-string");
        var hash3 = CryptoService.Sha256("different-input");

        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
        Assert.True(hash1.All(c => char.IsAsciiHexDigit(c) && (!char.IsLetter(c) || char.IsUpper(c))));
    }

    [Fact]
    public async Task SettingsService_GetAsync_ReturnsDefaultWhenNotOverridden()
    {
        var (service, _, _, _) = CreateSettingsService("SettingsTest_Defaults_" + Guid.NewGuid());

        var timeout = await service.GetIntAsync("DefaultTimeoutSeconds");
        var allowSelfApproval = await service.GetBoolAsync("AllowSelfApproval");
        var historyLimit = await service.GetAsync("HistoryLimit");

        Assert.Equal(30, timeout);
        Assert.False(allowSelfApproval);
        Assert.Equal("100", historyLimit);
    }

    [Fact]
    public async Task SettingsService_SetAsync_UpdatesValueAndCache()
    {
        var (service, db, _, _) = CreateSettingsService("SettingsTest_Update_" + Guid.NewGuid());

        await service.SetAsync("DefaultTimeoutSeconds", "60");

        var updatedValue = await service.GetIntAsync("DefaultTimeoutSeconds");
        Assert.Equal(60, updatedValue);

        // Verify persisted in database
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == "DefaultTimeoutSeconds");
        Assert.NotNull(entity);
        Assert.Equal("60", entity.Value);
    }

    [Fact]
    public async Task SettingsService_SetAsync_ThrowsOnUnknownKey()
    {
        var (service, _, _, _) = CreateSettingsService("SettingsTest_Unknown_" + Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await service.SetAsync("NonExistentCustomSetting", "value");
        });
    }

    [Fact]
    public async Task SettingsService_GetAllAsync_MergesDatabaseOverridesWithDefinitions()
    {
        var (service, _, _, _) = CreateSettingsService("SettingsTest_GetAll_" + Guid.NewGuid());

        await service.SetAsync("DefaultMaxRows", "2500");

        var all = await service.GetAllAsync();
        Assert.True(all.ContainsKey("DefaultTimeoutSeconds"));
        Assert.Equal("2500", all["DefaultMaxRows"]);
        Assert.Equal("30", all["DefaultTimeoutSeconds"]);
    }

    [Fact]
    public async Task SettingsService_SmtpConfig_SavesAndRetrievesWithEncryptedPassword()
    {
        var (service, _, crypto, _) = CreateSettingsService("SettingsTest_Smtp_" + Guid.NewGuid());

        await service.SaveSmtpConfigAsync(
            crypto,
            host: "smtp.mailprovider.com",
            port: 587,
            enableSsl: true,
            user: "mailer@example.com",
            newPassword: "SecretSmtpPassword123!",
            from: "notifications@example.com",
            fromName: "Query Gate Alerts");

        var smtp = await service.GetSmtpConfigAsync(crypto);

        Assert.True(smtp.IsConfigured);
        Assert.Equal("smtp.mailprovider.com", smtp.Host);
        Assert.Equal(587, smtp.Port);
        Assert.True(smtp.EnableSsl);
        Assert.Equal("mailer@example.com", smtp.User);
        Assert.Equal("SecretSmtpPassword123!", smtp.Password);
        Assert.Equal("notifications@example.com", smtp.From);
        Assert.Equal("Query Gate Alerts", smtp.FromName);
        Assert.True(smtp.HasSavedPassword);
    }

    [Fact]
    public async Task SettingsService_SmtpConfig_FallbackIncludesFromName()
    {
        var (service, _, crypto, _) = CreateSettingsService("SettingsTest_SmtpFallback_" + Guid.NewGuid());
        var fallback = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"] = "smtp.fallback.com",
                ["Smtp:Port"] = "2525",
                ["Smtp:From"] = "alerts@fallback.com",
                ["Smtp:FromName"] = "Fallback Alerts"
            })
            .Build();

        var smtp = await service.GetSmtpConfigAsync(crypto, fallback);

        Assert.True(smtp.IsConfigured);
        Assert.Equal("alerts@fallback.com", smtp.From);
        Assert.Equal("Fallback Alerts", smtp.FromName);
    }

    [Fact]
    public async Task AuditService_WritesAndTruncatesFields()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("AuditTest_" + Guid.NewGuid())
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var audit = new AuditService(factory, NullLogger<AuditService>.Instance);

        var longUserAgent = new string('U', 800);
        var actor = new Actor("user-audit-1", "alice", "192.168.1.10", longUserAgent);

        await audit.TryWriteAsync(
            actor,
            category: "Authentication",
            action: "Login",
            outcome: "Success",
            details: "User logged in with MFA");

        await using var db = factory.CreateDbContext();
        var entry = await db.AuditLogs.FirstOrDefaultAsync();

        Assert.NotNull(entry);
        Assert.Equal("user-audit-1", entry.UserId);
        Assert.Equal("alice", entry.UserName);
        Assert.Equal("192.168.1.10", entry.ClientIp);
        Assert.Equal(512, entry.UserAgent?.Length);
        Assert.Equal("Authentication", entry.Category);
        Assert.Equal("Login", entry.Action);
        Assert.Equal("Success", entry.Outcome);
        Assert.Equal("User logged in with MFA", entry.Details);
    }

    [Fact]
    public void AuditService_Truncate_HandlesNullAndBounds()
    {
        Assert.Null(AuditService.Truncate(null, 10));
        Assert.Equal("short", AuditService.Truncate("short", 10));
        Assert.Equal("12345", AuditService.Truncate("1234567890", 5));
    }
}
