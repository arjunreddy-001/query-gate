using DbAccessManager.Data;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Security;

public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, ITenantContextAccessor tenantAccessor)
    {
        tenantAccessor.Current = tenantContext;
        var path = context.Request.Path;

        // Skip static asset requests and health checks
        if (path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/_content") ||
            path.StartsWithSegments("/health") ||
            path.StartsWithSegments("/favicon.svg") ||
            path.Value?.EndsWith(".css") == true ||
            path.Value?.EndsWith(".js") == true ||
            path.Value?.EndsWith(".map") == true)
        {
            await next(context);
            return;
        }

        // 1. Check if platform administration area
        if (path.StartsWithSegments("/platform"))
        {
            tenantContext.SetPlatformAdmin();
            await next(context);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // 2. Custom Domain Resolution
        var host = context.Request.Host.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
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

                if (tenant is not null)
                {
                    tenantContext.SetTenant(tenant.Id, tenant.Slug, tenant.Name);
                    await next(context);
                    return;
                }
            }
        }

        // 3. Path-prefix Resolution: /t/{slug}/...
        if (path.StartsWithSegments("/t", out var afterT) && afterT.HasValue)
        {
            var segments = afterT.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                var slug = segments[0];
                var tenant = await db.Tenants
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Slug.ToLower() == slug.ToLower() && t.IsActive);

                if (tenant is not null)
                {
                    tenantContext.SetTenant(tenant.Id, tenant.Slug, tenant.Name);

                    // Adjust PathBase and Path for downstream routing
                    context.Request.PathBase = context.Request.PathBase.Add($"/t/{slug}");
                    var remaining = afterT.Value[(slug.Length + 1)..];
                    context.Request.Path = string.IsNullOrEmpty(remaining) ? "/" : remaining;

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
            var tenant = await db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug.ToLower() == headerSlug.ToLower() && t.IsActive);

            if (tenant is not null)
            {
                tenantContext.SetTenant(tenant.Id, tenant.Slug, tenant.Name);
                await next(context);
                return;
            }
        }

        // 5. Single-Tenant Backward Compatibility Fallback
        // If there is only one active tenant, use it automatically for app routes that are not public landing pages
        if (!tenantContext.IsResolved &&
            !path.StartsWithSegments("/select-tenant") &&
            path.Value != "/" &&
            path.Value != "/documentation")
        {
            var activeTenants = await db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => t.IsActive)
                .Take(2)
                .ToListAsync();

            if (activeTenants.Count == 1)
            {
                var single = activeTenants[0];
                tenantContext.SetTenant(single.Id, single.Slug, single.Name);
            }
        }

        await next(context);
    }
}
