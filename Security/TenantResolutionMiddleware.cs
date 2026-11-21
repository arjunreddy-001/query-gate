using DbAccessManager.Data;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Security;

public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ITenantLookupCache tenantCache,
    ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, ITenantContextAccessor tenantAccessor)
    {
        tenantAccessor.Current = tenantContext;
        var path = context.Request.Path;

        // Skip static asset requests and health checks for root paths
        if (!path.StartsWithSegments("/t") &&
            (path.StartsWithSegments("/_framework") ||
             path.StartsWithSegments("/_content") ||
             path.StartsWithSegments("/health") ||
             path.StartsWithSegments("/favicon.svg") ||
             path.Value?.EndsWith(".css") == true ||
             path.Value?.EndsWith(".js") == true ||
             path.Value?.EndsWith(".map") == true ||
             path.Value?.EndsWith(".svg") == true ||
             path.Value?.EndsWith(".png") == true ||
             path.Value?.EndsWith(".ico") == true ||
             path.Value?.EndsWith(".woff") == true ||
             path.Value?.EndsWith(".woff2") == true))
        {
            await next(context);
            return;
        }

        // Check if Blazor SignalR connection at root /_blazor - resolve context from referer
        if (path.StartsWithSegments("/_blazor"))
        {
            var referer = context.Request.Headers.Referer.ToString();
            if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            {
                var refPath = refUri.AbsolutePath;
                if (refPath.StartsWith("/platform", StringComparison.OrdinalIgnoreCase))
                {
                    tenantContext.SetPlatformAdmin();
                    await next(context);
                    return;
                }
                if (refPath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase))
                {
                    var segs = refPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (segs.Length >= 2)
                    {
                        var refSlug = segs[1];
                        if (!tenantCache.TryGetBySlug(refSlug, out var refTenant))
                        {
                            await using var db = await dbFactory.CreateDbContextAsync();
                            var tenant = await db.Tenants
                                .IgnoreQueryFilters()
                                .AsNoTracking()
                                .FirstOrDefaultAsync(t => t.Slug.ToLower() == refSlug.ToLower() && t.IsActive);

                            refTenant = tenant is not null ? new CachedTenant(tenant.Id, tenant.Slug, tenant.Name, tenant.IsActive) : null;
                            tenantCache.SetBySlug(refSlug, refTenant);
                        }

                        if (refTenant is not null)
                        {
                            tenantContext.SetTenant(refTenant.Id, refTenant.Slug, refTenant.Name);
                            await next(context);
                            return;
                        }
                    }
                }
                if (refPath.Equals("/select-tenant", StringComparison.OrdinalIgnoreCase) ||
                    refPath.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                    refPath.Equals("/documentation", StringComparison.OrdinalIgnoreCase))
                {
                    await next(context);
                    return;
                }
            }
        }

        // 1. Check if platform administration area
        if (path.StartsWithSegments("/platform"))
        {
            tenantContext.SetPlatformAdmin();
            await next(context);
            return;
        }

        // 2. Custom Domain Resolution
        var host = context.Request.Host.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            if (!tenantCache.TryGetByDomain(host, out var domainTenant))
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var domainMatch = await db.TenantDomains
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Domain.ToLower() == host.ToLower() && d.IsVerified);

                if (domainMatch is not null)
                {
                    var tenant = await db.Tenants
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == domainMatch.TenantId && t.IsActive);

                    domainTenant = tenant is not null ? new CachedTenant(tenant.Id, tenant.Slug, tenant.Name, tenant.IsActive) : null;
                }
                tenantCache.SetByDomain(host, domainTenant);
            }

            if (domainTenant is not null)
            {
                tenantContext.SetTenant(domainTenant.Id, domainTenant.Slug, domainTenant.Name);
                await next(context);
                return;
            }
        }

        // 3. Path-prefix Resolution: /t/{slug}/...
        if (path.StartsWithSegments("/t", out var afterT) && afterT.HasValue)
        {
            var segments = afterT.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                var slug = segments[0];
                if (!tenantCache.TryGetBySlug(slug, out var slugTenant))
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    var tenant = await db.Tenants
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Slug.ToLower() == slug.ToLower() && t.IsActive);

                    slugTenant = tenant is not null ? new CachedTenant(tenant.Id, tenant.Slug, tenant.Name, tenant.IsActive) : null;
                    tenantCache.SetBySlug(slug, slugTenant);
                }

                if (slugTenant is not null)
                {
                    tenantContext.SetTenant(slugTenant.Id, slugTenant.Slug, slugTenant.Name);

                    // Adjust PathBase and Path for downstream routing
                    context.Request.PathBase = context.Request.PathBase.Add($"/t/{slug}");
                    var remaining = afterT.Value[(slug.Length + 1)..];
                    if (string.IsNullOrEmpty(remaining) || remaining == "/")
                    {
                        context.Response.Redirect($"{context.Request.PathBase}/dashboard");
                        return;
                    }
                    context.Request.Path = remaining;

                    await next(context);
                    return;
                }
                else
                {
                    logger.LogWarning("Requested tenant slug '{Slug}' was not found or is inactive", slug);
                }
            }
        }

        // 4. Header or Claim fallback for API/JWT calls
        var headerSlug = context.Request.Headers["X-Tenant-Slug"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerSlug))
        {
            if (!tenantCache.TryGetBySlug(headerSlug, out var headerTenant))
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var tenant = await db.Tenants
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Slug.ToLower() == headerSlug.ToLower() && t.IsActive);

                headerTenant = tenant is not null ? new CachedTenant(tenant.Id, tenant.Slug, tenant.Name, tenant.IsActive) : null;
                tenantCache.SetBySlug(headerSlug, headerTenant);
            }

            if (headerTenant is not null)
            {
                tenantContext.SetTenant(headerTenant.Id, headerTenant.Slug, headerTenant.Name);
                await next(context);
                return;
            }
        }

        // 5. Default Workspace Fallback for un-prefixed app routes
        if (!tenantContext.IsResolved &&
            !tenantContext.IsPlatformAdmin &&
            !path.StartsWithSegments("/platform") &&
            !path.StartsWithSegments("/select-tenant") &&
            !path.StartsWithSegments("/Account") &&
            !path.StartsWithSegments("/api") &&
            !path.StartsWithSegments("/_blazor") &&
            path.Value != "/" &&
            path.Value != "/not-found" &&
            path.Value != "/documentation")
        {
            var targetSlug = context.User.FindFirst("preferred_tenant_slug")?.Value
                ?? context.Request.Cookies["QueryGate_DefaultWorkspace"]
                ?? "default";

            if (!tenantCache.TryGetBySlug(targetSlug, out var targetTenant))
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var tenant = await db.Tenants
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Slug.ToLower() == targetSlug.ToLower() && t.IsActive);

                targetTenant = tenant is not null ? new CachedTenant(tenant.Id, tenant.Slug, tenant.Name, tenant.IsActive) : null;
                tenantCache.SetBySlug(targetSlug, targetTenant);
            }

            if (targetTenant is null && targetSlug != "default")
            {
                if (!tenantCache.TryGetBySlug("default", out targetTenant))
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    var tenant = await db.Tenants
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Slug == "default" && t.IsActive);

                    targetTenant = tenant is not null ? new CachedTenant(tenant.Id, tenant.Slug, tenant.Name, tenant.IsActive) : null;
                    tenantCache.SetBySlug("default", targetTenant);
                }
            }

            if (targetTenant is not null)
            {
                context.Response.Redirect($"/t/{targetTenant.Slug}{path}");
                return;
            }
        }

        await next(context);
    }
}
