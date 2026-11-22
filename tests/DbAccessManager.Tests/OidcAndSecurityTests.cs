using System.Net;
using System.Text.Json;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbAccessManager.Tests;

public class OidcAndSecurityTests
{
    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    private sealed class MockHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new DelegatingMockHandler(handlerFunc));
        }
    }

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> func) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(func(request));
    }

    // --- OidcService Tests ---

    [Fact]
    public async Task OidcService_SaveAndRetrieveConfig_EncryptedSecretRoundtrip()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("OidcTests_" + Guid.NewGuid())
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
        }).Build();
        var crypto = new CryptoService(config);
        var settings = new SettingsService(factory);
        var httpFactory = new MockHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var oidc = new OidcService(httpFactory, settings, crypto);

        Assert.False(await oidc.IsEnabledAsync());

        await oidc.SaveOidcConfigAsync(
            enabled: true,
            displayName: "Okta SSO",
            authority: "https://example.okta.com",
            clientId: "okta-client-id",
            rawClientSecret: "super-secret-client-secret-987",
            scopes: "openid profile email groups");

        Assert.True(await oidc.IsEnabledAsync());
        Assert.Equal("Okta SSO", await settings.GetAsync(OidcService.SettingsKeyDisplayName));
        Assert.Equal("https://example.okta.com", await settings.GetAsync(OidcService.SettingsKeyAuthority));
        Assert.Equal("okta-client-id", await settings.GetAsync(OidcService.SettingsKeyClientId));
        Assert.Equal("openid profile email groups", await settings.GetAsync(OidcService.SettingsKeyScopes));

        var decryptedSecret = await oidc.GetDecryptedClientSecretAsync();
        Assert.Equal("super-secret-client-secret-987", decryptedSecret);
    }

    [Fact]
    public async Task OidcService_TestDiscoveryAsync_ValidatesInputAndResponses()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("OidcDiscoveryTests_" + Guid.NewGuid())
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
        }).Build();
        var crypto = new CryptoService(config);
        var settings = new SettingsService(factory);

        // 1. Empty authority
        var oidc1 = new OidcService(new MockHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)), settings, crypto);
        var (ok1, msg1) = await oidc1.TestDiscoveryAsync("");
        Assert.False(ok1);
        Assert.Contains("Authority / Issuer URL cannot be empty", msg1);

        // 2. HTTP 404
        var oidc2 = new OidcService(new MockHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), settings, crypto);
        var (ok2, msg2) = await oidc2.TestDiscoveryAsync("https://auth.company.test");
        Assert.False(ok2);
        Assert.Contains("HTTP 404", msg2);

        // 3. Incomplete discovery JSON
        var incompleteJson = JsonSerializer.Serialize(new { something_else = "value" });
        var oidc3 = new OidcService(new MockHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(incompleteJson)
        }), settings, crypto);
        var (ok3, msg3) = await oidc3.TestDiscoveryAsync("https://auth.company.test");
        Assert.False(ok3);
        Assert.Contains("missing required issuer or authorization_endpoint", msg3);

        // 4. Valid discovery document
        var validJson = JsonSerializer.Serialize(new
        {
            issuer = "https://auth.company.test",
            authorization_endpoint = "https://auth.company.test/oauth2/v1/authorize",
            token_endpoint = "https://auth.company.test/oauth2/v1/token"
        });
        var oidc4 = new OidcService(new MockHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(validJson)
        }), settings, crypto);
        var (ok4, msg4) = await oidc4.TestDiscoveryAsync("https://auth.company.test");
        Assert.True(ok4);
        Assert.Contains("Valid OIDC discovery document found", msg4);
    }

    // --- UserStatusCache Tests ---

    [Fact]
    public void CachedUserStatus_CanSignIn_EvaluatesActiveAndExpiry()
    {
        var now = DateTimeOffset.UtcNow;

        // Active with no expiry
        var s1 = new CachedUserStatus("u1", "u1@test.com", true, null, null, null);
        Assert.True(s1.CanSignIn(now));

        // Active with future expiry
        var s2 = new CachedUserStatus("u2", "u2@test.com", true, now.AddHours(2), null, null);
        Assert.True(s2.CanSignIn(now));

        // Active with past expiry
        var s3 = new CachedUserStatus("u3", "u3@test.com", true, now.AddHours(-1), null, null);
        Assert.False(s3.CanSignIn(now));

        // Inactive
        var s4 = new CachedUserStatus("u4", "u4@test.com", false, null, null, null);
        Assert.False(s4.CanSignIn(now));
    }

    [Fact]
    public async Task UserStatusCache_GetOrLoadAsync_And_Invalidate()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var userStatusCache = new UserStatusCache(cache);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(opt => opt.UseInMemoryDatabase("UserStatusTests_" + Guid.NewGuid()));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        var provider = services.BuildServiceProvider();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        // Seed a user
        var user = new ApplicationUser
        {
            Id = "user-status-test",
            UserName = "bob@example.com",
            Email = "bob@example.com",
            IsActive = true,
            TimeoutOverrideSeconds = 120
        };
        await userManager.CreateAsync(user);

        // Load into cache
        var status1 = await userStatusCache.GetOrLoadAsync("user-status-test", userManager);
        Assert.NotNull(status1);
        Assert.Equal("user-status-test", status1.Id);
        Assert.True(status1.IsActive);
        Assert.Equal(120, status1.TimeoutOverrideSeconds);

        // From cache
        var status2 = await userStatusCache.GetOrLoadAsync("user-status-test", userManager);
        Assert.Same(status1, status2);

        // Non-existent user cached as null
        var missingStatus = await userStatusCache.GetOrLoadAsync("non-existent-user", userManager);
        Assert.Null(missingStatus);

        // Invalidate single user
        userStatusCache.Invalidate("user-status-test");
        var statusAfterInvalidate = await userStatusCache.GetOrLoadAsync("user-status-test", userManager);
        Assert.NotNull(statusAfterInvalidate);
        Assert.NotSame(status1, statusAfterInvalidate);

        // Invalidate all
        userStatusCache.Invalidate();
    }

    // --- TenantAwareDbContextFactory Tests ---

    [Fact]
    public void TenantAwareDbContextFactory_CreateDbContext_AttachesActiveTenant()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("TenantFactoryTests_" + Guid.NewGuid())
            .Options;

        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId, "test-tenant", "Test Workspace");

        var tenantAccessor = new TenantContextAccessor { Current = tenantContext };
        var httpAccessor = new HttpContextAccessor();

        var factory = new TenantAwareDbContextFactory(options, httpAccessor, tenantAccessor);

        using var db = factory.CreateDbContext();
        Assert.NotNull(db.Tenant);
        Assert.Equal(tenantId, db.CurrentTenantId);
        Assert.Equal("test-tenant", db.Tenant.Slug);
    }

    [Fact]
    public async Task TenantAwareDbContextFactory_CreateDbContextAsync_AttachesActiveTenant()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("TenantFactoryAsyncTests_" + Guid.NewGuid())
            .Options;

        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId, "async-tenant", "Async Workspace");

        var tenantAccessor = new TenantContextAccessor { Current = tenantContext };
        var httpAccessor = new HttpContextAccessor();

        var factory = new TenantAwareDbContextFactory(options, httpAccessor, tenantAccessor);

        await using var db = await factory.CreateDbContextAsync();
        Assert.NotNull(db.Tenant);
        Assert.Equal(tenantId, db.CurrentTenantId);
        Assert.Equal("async-tenant", db.Tenant.Slug);
    }
}
