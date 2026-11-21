namespace DbAccessManager.Data;

/// <summary>
/// Reserved tenant IDs used by the platform itself.
/// </summary>
public static class WellKnownTenants
{
    /// <summary>
    /// Platform-level (non-tenant) users such as SuperAdmin accounts use this sentinel TenantId.
    /// </summary>
    public static readonly Guid Platform = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Fallback default workspace for single-tenant or initial configurations.
    /// </summary>
    public static readonly Guid Default = new("00000000-0000-0000-0000-000000000002");
}
