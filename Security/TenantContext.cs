using DbAccessManager.Data;

namespace DbAccessManager.Security;

public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public string? Slug { get; private set; }
    public string? Name { get; private set; }
    public bool IsResolved => TenantId.HasValue && TenantId.Value != Guid.Empty && !IsPlatformAdmin;
    public bool IsPlatformAdmin { get; private set; }

    public void SetTenant(Guid tenantId, string slug, string name)
    {
        TenantId = tenantId;
        Slug = slug;
        Name = name;
        IsPlatformAdmin = false;
    }

    public void SetPlatformAdmin()
    {
        TenantId = WellKnownTenants.Platform;
        Slug = "platform";
        Name = "Platform Administration";
        IsPlatformAdmin = true;
    }

    public void Reset()
    {
        TenantId = null;
        Slug = null;
        Name = null;
        IsPlatformAdmin = false;
    }
}
