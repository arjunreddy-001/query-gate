using DbAccessManager.Security;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace DbAccessManager.Services;

public sealed class TenantCircuitHandler(
    IHttpContextAccessor httpAccessor,
    ITenantContext scopedTenant,
    ITenantContextAccessor tenantAccessor) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var ctx = httpAccessor.HttpContext;
        if (ctx is not null)
        {
            var httpTenant = ctx.RequestServices.GetService<ITenantContext>();
            if (httpTenant is not null)
            {
                if (httpTenant.IsResolved && httpTenant.TenantId.HasValue && scopedTenant is TenantContext tc)
                {
                    tc.SetTenant(httpTenant.TenantId.Value, httpTenant.Slug ?? "", httpTenant.Name ?? "");
                }
                else if (httpTenant.IsPlatformAdmin && scopedTenant is TenantContext tcAdmin)
                {
                    tcAdmin.SetPlatformAdmin();
                }
            }
        }

        tenantAccessor.Current = scopedTenant;
        return Task.CompletedTask;
    }
}
