using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class TenantResolutionMiddlewareTests
{
    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    private static (TenantResolutionMiddleware Middleware, ITenantLookupCache Cache, IDbContextFactory<ApplicationDbContext> Factory, List<HttpContext> NextInvocations)
        CreateMiddleware()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("TenantMiddlewareTests_" + Guid.NewGuid())
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var cache = new TenantLookupCache();
        var nextInvocations = new List<HttpContext>();

        RequestDelegate next = ctx =>
        {
            nextInvocations.Add(ctx);
            return Task.CompletedTask;
        };

        var middleware = new TenantResolutionMiddleware(
            next,
            factory,
            cache,
            NullLogger<TenantResolutionMiddleware>.Instance);

        return (middleware, cache, factory, nextInvocations);
    }

    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/something/bundle.js")]
    [InlineData("/health")]
    [InlineData("/favicon.svg")]
    [InlineData("/app.css")]
    [InlineData("/site.js")]
    [InlineData("/font.woff2")]
    public async Task InvokeAsync_SkipsResolutionForStaticAssets(string path)
    {
        var (middleware, _, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.False(tenantContext.IsResolved);
        Assert.False(tenantContext.IsPlatformAdmin);
    }

    [Fact]
    public async Task InvokeAsync_SetsPlatformAdminForPlatformPath()
    {
        var (middleware, _, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/platform/tenants";

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.True(tenantContext.IsPlatformAdmin);
        Assert.False(tenantContext.IsResolved);
    }

    [Fact]
    public async Task InvokeAsync_ResolvesTenantFromCustomDomain()
    {
        var (middleware, cache, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("corp.querygate.local");
        context.Request.Path = "/query";

        var tenantId = Guid.NewGuid();
        cache.SetByDomain("corp.querygate.local", new CachedTenant(tenantId, "corp", "Corp Workspace", true));

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.True(tenantContext.IsResolved);
        Assert.Equal(tenantId, tenantContext.TenantId);
        Assert.Equal("corp", tenantContext.Slug);
        Assert.Equal("Corp Workspace", tenantContext.Name);
    }

    [Fact]
    public async Task InvokeAsync_ResolvesTenantFromPathPrefix()
    {
        var (middleware, cache, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/t/acme/query";

        var tenantId = Guid.NewGuid();
        cache.SetBySlug("acme", new CachedTenant(tenantId, "acme", "Acme Inc", true));

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.True(tenantContext.IsResolved);
        Assert.Equal(tenantId, tenantContext.TenantId);
        Assert.Equal("acme", tenantContext.Slug);
        Assert.Equal("/t/acme", context.Request.PathBase.Value);
        Assert.Equal("/query", context.Request.Path.Value);
    }

    [Fact]
    public async Task InvokeAsync_RedirectsToDashboardWhenPathIsTenantRoot()
    {
        var (middleware, cache, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/t/acme/";

        var tenantId = Guid.NewGuid();
        cache.SetBySlug("acme", new CachedTenant(tenantId, "acme", "Acme Inc", true));

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Empty(nextCalls); // Request terminated by redirect
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("/t/acme/dashboard", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task InvokeAsync_ResolvesTenantFromHeader()
    {
        var (middleware, cache, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/query";
        context.Request.Headers["X-Tenant-Slug"] = "partner";

        var tenantId = Guid.NewGuid();
        cache.SetBySlug("partner", new CachedTenant(tenantId, "partner", "Partner Portal", true));

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.True(tenantContext.IsResolved);
        Assert.Equal(tenantId, tenantContext.TenantId);
        Assert.Equal("partner", tenantContext.Slug);
    }

    [Fact]
    public async Task InvokeAsync_ResolvesContextFromBlazorSignalRReferer()
    {
        var (middleware, cache, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/_blazor";
        context.Request.Headers.Referer = "http://localhost:5175/t/fintech/dashboard";

        var tenantId = Guid.NewGuid();
        cache.SetBySlug("fintech", new CachedTenant(tenantId, "fintech", "Fintech Org", true));

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.True(tenantContext.IsResolved);
        Assert.Equal(tenantId, tenantContext.TenantId);
        Assert.Equal("fintech", tenantContext.Slug);
    }

    [Fact]
    public async Task InvokeAsync_ResolvesPlatformAdminFromBlazorReferer()
    {
        var (middleware, _, _, nextCalls) = CreateMiddleware();
        var context = new DefaultHttpContext();
        context.Request.Path = "/_blazor";
        context.Request.Headers.Referer = "http://localhost:5175/platform/tenants";

        var tenantContext = new TenantContext();
        var tenantAccessor = new TenantContextAccessor();

        await middleware.InvokeAsync(context, tenantContext, tenantAccessor);

        Assert.Single(nextCalls);
        Assert.True(tenantContext.IsPlatformAdmin);
        Assert.False(tenantContext.IsResolved);
    }
}
