namespace DbAccessManager.Security;

public interface ITenantContext
{
    Guid? TenantId { get; }
    string? Slug { get; }
    string? Name { get; }
    bool IsResolved { get; }
    bool IsPlatformAdmin { get; }
    void SetTenant(Guid tenantId, string slug, string name);
    void SetPlatformAdmin();
    void Reset();
}
