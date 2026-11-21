using System.Collections.Concurrent;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbAccessManager.Tests;

public class CachingTests
{
    private static readonly Actor TestActor = new("admin1", "admin@example.com", "127.0.0.1", "TestAgent");

    [Fact]
    public void TenantLookupCache_StoresAndRetrieves_BySlugAndDomain()
    {
        var cache = new TenantLookupCache();
        var tenantId = Guid.NewGuid();
        var cached = new CachedTenant(tenantId, "acme", "Acme Corp", true);

        cache.SetBySlug("acme", cached);
        cache.SetByDomain("acme.querygate.local", cached);

        var foundBySlug = cache.TryGetBySlug("acme", out var tenantFromSlug);
        var foundByDomain = cache.TryGetByDomain("acme.querygate.local", out var tenantFromDomain);

        Assert.True(foundBySlug);
        Assert.NotNull(tenantFromSlug);
        Assert.Equal("acme", tenantFromSlug.Slug);
        Assert.Equal(tenantId, tenantFromSlug.Id);

        Assert.True(foundByDomain);
        Assert.NotNull(tenantFromDomain);
        Assert.Equal("acme", tenantFromDomain.Slug);

        // Case-insensitivity check
        Assert.True(cache.TryGetBySlug("ACME", out var tenantUpper));
        Assert.Equal(tenantId, tenantUpper!.Id);

        // Invalidate clears cache
        cache.Invalidate();
        Assert.False(cache.TryGetBySlug("acme", out _));
        Assert.False(cache.TryGetByDomain("acme.querygate.local", out _));
    }

    [Fact]
    public void TenantLookupCache_NegativeCaching_PreventsRepeatedMisses()
    {
        var cache = new TenantLookupCache();

        // Record a negative cache result (null tenant)
        cache.SetBySlug("missing-tenant", null);

        var found = cache.TryGetBySlug("missing-tenant", out var tenant);
        Assert.True(found);
        Assert.Null(tenant);

        cache.Invalidate();
        var foundAfterInvalidate = cache.TryGetBySlug("missing-tenant", out _);
        Assert.False(foundAfterInvalidate);
    }

    [Fact]
    public void TenantLookupCache_SingleTenantFallback_WorksAndInvalidates()
    {
        var cache = new TenantLookupCache();
        var tenantId = Guid.NewGuid();
        var cached = new CachedTenant(tenantId, "single", "Single Tenant", true);

        cache.SetSingleActive(true, cached);
        var found = cache.TryGetSingleActive(out var isSingle, out var fallback);

        Assert.True(found);
        Assert.True(isSingle);
        Assert.NotNull(fallback);
        Assert.Equal(tenantId, fallback.Id);

        cache.Invalidate();
        Assert.False(cache.TryGetSingleActive(out _, out _));
    }

    [Fact]
    public void SqlGate_CachesAnalysis_AndClonesSafely()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var gate = new SqlGate(memoryCache);

        const string sql = "SELECT id, name FROM users WHERE id = 1";
        var analysis1 = gate.Analyze(sql, DatabaseEngine.SqlServer);
        var analysis2 = gate.Analyze(sql, DatabaseEngine.SqlServer);

        Assert.NotNull(analysis1);
        Assert.NotNull(analysis2);
        Assert.True(analysis1.Parsed);
        Assert.True(analysis2.Parsed);
        Assert.Contains(Perm.Select, analysis1.Requirements);
        Assert.True(analysis1.IsReadOnly);

        // Ensure returned objects are distinct instances (cloned) to avoid callers corrupting cache
        Assert.NotSame(analysis1, analysis2);

        // Clear cache and verify fresh run
        gate.ClearCache();
        var analysis3 = gate.Analyze(sql, DatabaseEngine.SqlServer);
        Assert.NotNull(analysis3);
        Assert.NotSame(analysis1, analysis3);
    }

    [Fact]
    public void CryptoService_CachesDecryptedValues()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Encryption:Key"] = Convert.ToBase64String(new byte[32]) // 256-bit test key
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var crypto = new CryptoService(config);

        const string plaintext = "Server=localhost;Database=TestDb;User Id=sa;Password=secret;";
        var ciphertext = crypto.Encrypt(plaintext);

        var decrypted1 = crypto.Decrypt(ciphertext);
        var decrypted2 = crypto.Decrypt(ciphertext);

        Assert.Equal(plaintext, decrypted1);
        Assert.Equal(plaintext, decrypted2);
        Assert.Same(decrypted1, decrypted2); // Decryption cache should return the exact cached string reference

        crypto.ClearCache();
        var decrypted3 = crypto.Decrypt(ciphertext);
        Assert.Equal(plaintext, decrypted3);
    }

    [Fact]
    public async Task DatabaseCache_CachesDescriptor_AndInvalidates()
    {
        var services = new ServiceCollection();
        var dbName = "DbCacheTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var crypto = new CryptoService(config);
        services.AddSingleton(crypto);

        var memCache = new MemoryCache(new MemoryCacheOptions());
        services.AddSingleton<IMemoryCache>(memCache);
        services.AddSingleton<IDatabaseCache, DatabaseCache>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var dbCache = provider.GetRequiredService<IDatabaseCache>();

        var dbId = Guid.NewGuid();
        const string rawConn = "Server=test-db;Database=app;Uid=test;Pwd=pass;";
        var encConn = crypto.Encrypt(rawConn);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "Production Main",
                Environment = "Production",
                Engine = DatabaseEngine.PostgreSql,
                EncryptedConnectionString = encConn,
                DefaultTimeoutSeconds = 45,
                MaxRows = 5000,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        // First call populates cache
        var desc1 = await dbCache.GetAsync(dbId);
        Assert.NotNull(desc1);
        Assert.Equal("Production Main", desc1.Name);
        Assert.Equal(rawConn, desc1.DecryptedConnectionString);
        Assert.Equal(DatabaseEngine.PostgreSql, desc1.Engine);

        // Second call retrieves cached descriptor instance
        var desc2 = await dbCache.GetAsync(dbId);
        Assert.NotNull(desc2);
        Assert.Same(desc1, desc2);

        // Mutate raw DB row directly to simulate out-of-band change
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.ManagedDatabases.FindAsync(dbId);
            row!.Name = "Production Renamed";
            await db.SaveChangesAsync();
        }

        // Before invalidation: still returns cached old descriptor
        var descBeforeInvalidate = await dbCache.GetAsync(dbId);
        Assert.Equal("Production Main", descBeforeInvalidate!.Name);

        // After invalidation: reloads new name from database
        dbCache.Invalidate(dbId);
        var descAfterInvalidate = await dbCache.GetAsync(dbId);
        Assert.NotNull(descAfterInvalidate);
        Assert.Equal("Production Renamed", descAfterInvalidate.Name);
    }

    [Fact]
    public async Task UserStatusCache_CachesUser_AndInvalidates()
    {
        var services = new ServiceCollection();
        var dbName = "UserStatusCacheTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton<IUserStatusCache, UserStatusCache>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var statusCache = provider.GetRequiredService<IUserStatusCache>();

        var userId = "user-" + Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "bob@example.com",
                Email = "bob@example.com",
                IsActive = true,
                TimeoutOverrideSeconds = 120
            });
            await db.SaveChangesAsync();
        }

        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var status1 = await statusCache.GetOrLoadAsync(userId, userManager);
            Assert.NotNull(status1);
            Assert.True(status1.IsActive);
            Assert.Equal(120, status1.TimeoutOverrideSeconds);
        }

        // Update user directly in database
        await using (var db = await factory.CreateDbContextAsync())
        {
            var u = await db.Users.FindAsync(userId);
            u!.IsActive = false;
            await db.SaveChangesAsync();
        }

        // Cached value returns true before invalidation
        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var statusBefore = await statusCache.GetOrLoadAsync(userId, userManager);
            Assert.True(statusBefore!.IsActive);
        }

        // After invalidation: returns false
        statusCache.Invalidate(userId);
        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var statusAfter = await statusCache.GetOrLoadAsync(userId, userManager);
            Assert.NotNull(statusAfter);
            Assert.False(statusAfter.IsActive);
        }
    }

    [Fact]
    public async Task SettingsService_CachesRawSettings_AndUpdatesOnSet()
    {
        var services = new ServiceCollection();
        var dbName = "SettingsServiceCacheTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        var settingsService = new SettingsService(factory, memCache);

        // Set setting
        await settingsService.SetRawAsync("CustomTimeout", "99");

        // First read gets from cache
        var val1 = await settingsService.GetRawAsync("CustomTimeout");
        Assert.Equal("99", val1);

        // Update setting via SetRawAsync immediately updates cache
        await settingsService.SetRawAsync("CustomTimeout", "150");
        var val2 = await settingsService.GetRawAsync("CustomTimeout");
        Assert.Equal("150", val2);

        // Invalidate cache clears cached value
        settingsService.InvalidateCache();
        var val3 = await settingsService.GetRawAsync("CustomTimeout");
        Assert.Equal("150", val3);
    }

    [Fact]
    public async Task PermissionService_CachesRules_AndInvalidates()
    {
        var services = new ServiceCollection();
        var dbName = "PermissionServiceCacheTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        var permService = new PermissionService(factory, memCache);

        var dbId = Guid.NewGuid();
        var userId = "user-" + Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "permuser@example.com",
                Email = "permuser@example.com",
                IsActive = true
            });
            db.PermissionGrants.Add(new PermissionGrant
            {
                UserId = userId,
                DatabaseId = dbId,
                PermissionCode = Perm.Select,
                Effect = PermissionEffect.Allow,
                Schema = "*",
                ObjectName = "*"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var rules1 = await permService.GetRulesAsync(db, userId, dbId);
            Assert.Single(rules1);
            Assert.Equal(Perm.Select, rules1[0].PermissionCode);
        }

        // Add another grant directly to database
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PermissionGrants.Add(new PermissionGrant
            {
                UserId = userId,
                DatabaseId = dbId,
                PermissionCode = Perm.Insert,
                Effect = PermissionEffect.Allow,
                Schema = "*",
                ObjectName = "*"
            });
            await db.SaveChangesAsync();
        }

        // Before invalidation: still returns single grant from cache
        await using (var db = await factory.CreateDbContextAsync())
        {
            var rulesCached = await permService.GetRulesAsync(db, userId, dbId);
            Assert.Single(rulesCached);
        }

        // After invalidation: returns both grants
        permService.Invalidate(userId: userId, databaseId: dbId);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var rulesFresh = await permService.GetRulesAsync(db, userId, dbId);
            Assert.Equal(2, rulesFresh.Count);
        }
    }

    [Fact]
    public async Task DataMaskingService_TenantIsolatedCaching_AndInvalidation()
    {
        var services = new ServiceCollection();
        var dbName = "DataMaskingCacheTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        // Ensure cache is clear before test
        DataMaskingService.InvalidateCache();

        var (rulesDefault, enabledDefault) = await DataMaskingService.GetConfigAsync(factory);
        Assert.True(enabledDefault);
        Assert.NotEmpty(rulesDefault);

        // Modify config and save
        var modifiedRules = new List<MaskingRule>(rulesDefault)
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Custom Test Rule",
                Patterns = "custom_ssn",
                Strategy = DataMaskingService.MaskingStrategy.Ssn,
                IsEnabled = true
            }
        };

        await DataMaskingService.SaveConfigAsync(factory, modifiedRules, false);

        var (rulesAfterSave, enabledAfterSave) = await DataMaskingService.GetConfigAsync(factory);
        Assert.False(enabledAfterSave);
        Assert.Contains(rulesAfterSave, r => r.Name == "Custom Test Rule");

        // Clean up
        DataMaskingService.InvalidateCache();
    }

    [Fact]
    public async Task TenantResolutionMiddleware_TenantPrefix_RewritesPathAndPathBase_EvenForCss()
    {
        var services = new ServiceCollection();
        var dbName = "TenantMiddlewareTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        var cache = new TenantLookupCache();
        var tenantId = Guid.NewGuid();
        cache.SetBySlug("default", new CachedTenant(tenantId, "default", "Default Workspace", true));

        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            dbFactory: factory,
            tenantCache: cache,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantResolutionMiddleware>.Instance);

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Path = "/t/default/app.css";

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(httpContext, tenantContext, tenantAccessor);

        Assert.Equal("/t/default", httpContext.Request.PathBase.Value);
        Assert.Equal("/app.css", httpContext.Request.Path.Value);
        Assert.True(tenantContext.IsResolved);
        Assert.Equal(tenantId, tenantContext.TenantId);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_RootCss_SkipsWithoutChangingPath()
    {
        var services = new ServiceCollection();
        var dbName = "TenantMiddlewareTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        var cache = new TenantLookupCache();
        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            dbFactory: factory,
            tenantCache: cache,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantResolutionMiddleware>.Instance);

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Path = "/app.css";

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(httpContext, tenantContext, tenantAccessor);

        Assert.Equal("", httpContext.Request.PathBase.Value ?? "");
        Assert.Equal("/app.css", httpContext.Request.Path.Value);
        Assert.False(tenantContext.IsResolved);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_BlazorSignalR_ResolvesFromPlatformReferer()
    {
        var services = new ServiceCollection();
        var dbName = "TenantMiddlewareTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        var cache = new TenantLookupCache();
        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            dbFactory: factory,
            tenantCache: cache,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantResolutionMiddleware>.Instance);

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Path = "/_blazor";
        httpContext.Request.Headers.Referer = "http://localhost:5175/platform/tenants";

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(httpContext, tenantContext, tenantAccessor);

        Assert.True(tenantContext.IsPlatformAdmin);
        Assert.False(tenantContext.IsResolved);
    }

    [Fact]
    public async Task TenantResolutionMiddleware_Unprefixed_RedirectsToPreferredTenant()
    {
        var services = new ServiceCollection();
        var dbName = "TenantMiddlewareMultiTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        // Seed 2 active tenants
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new Tenant { Name = "Default Workspace", Slug = "default", IsActive = true });
            db.Tenants.Add(new Tenant { Name = "Tenant 2", Slug = "t2", IsActive = true });
            await db.SaveChangesAsync();
        }

        var cache = new TenantLookupCache();
        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            dbFactory: factory,
            tenantCache: cache,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantResolutionMiddleware>.Instance);

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Path = "/dashboard";
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim("preferred_tenant_slug", "t2")
            ], "TestAuth"));

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(httpContext, tenantContext, tenantAccessor);

        Assert.Equal(302, httpContext.Response.StatusCode);
        Assert.Equal("/t/t2/dashboard", httpContext.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task TenantResolutionMiddleware_Unprefixed_FallsBackToDefaultTenant()
    {
        var services = new ServiceCollection();
        var dbName = "TenantMiddlewareDefaultTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        // Seed default tenant
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Tenants.Add(new Tenant { Name = "Default Workspace", Slug = "default", IsActive = true });
            await db.SaveChangesAsync();
        }

        var cache = new TenantLookupCache();
        var middleware = new TenantResolutionMiddleware(
            next: ctx => Task.CompletedTask,
            dbFactory: factory,
            tenantCache: cache,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantResolutionMiddleware>.Instance);

        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Request.Path = "/dashboard";

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(httpContext, tenantContext, tenantAccessor);

        Assert.Equal(302, httpContext.Response.StatusCode);
        Assert.Equal("/t/default/dashboard", httpContext.Response.Headers.Location.ToString());
    }
}
