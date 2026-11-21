using DbAccessManager.Security;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Data;

public sealed class TenantAwareDbContextFactory(
    DbContextOptions<ApplicationDbContext> options,
    IHttpContextAccessor httpAccessor,
    ITenantContextAccessor tenantAccessor) : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext()
    {
        return new ApplicationDbContext(options, ResolveTenant());
    }

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ApplicationDbContext(options, ResolveTenant()));
    }

    private ITenantContext? ResolveTenant()
    {
        if (tenantAccessor.Current is not null && tenantAccessor.Current.TenantId.HasValue)
        {
            return tenantAccessor.Current;
        }

        var httpTenant = httpAccessor.HttpContext?.RequestServices.GetService<ITenantContext>();
        if (httpTenant is not null && httpTenant.TenantId.HasValue)
        {
            return httpTenant;
        }

        return tenantAccessor.Current ?? TenantContextAccessor.FallbackDefaultTenant;
    }
}
