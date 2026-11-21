using DbAccessManager.Data;

namespace DbAccessManager.Security;

public interface ITenantContextAccessor
{
    ITenantContext? Current { get; set; }
}

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<ITenantContext?> _current = new();
    private static ITenantContext? _fallbackDefault;

    public static Guid FallbackTenantId => _fallbackDefault?.TenantId ?? WellKnownTenants.Default;

    public static void SetFallbackDefaultTenant(Guid tenantId, string slug, string name)
    {
        var ctx = new TenantContext();
        ctx.SetTenant(tenantId, slug, name);
        _fallbackDefault = ctx;
    }

    public static ITenantContext? FallbackDefaultTenant => _fallbackDefault;

    public ITenantContext? Current
    {
        get => _current.Value ?? _fallbackDefault;
        set => _current.Value = value;
    }
}
