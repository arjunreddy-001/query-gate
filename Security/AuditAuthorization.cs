using System.Security.Claims;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Authorization;

namespace DbAccessManager.Security;

public sealed class AuditRequirement : IAuthorizationRequirement;

public sealed class AuditAuthorizationHandler(AccessConfigurationService access) : AuthorizationHandler<AuditRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AuditRequirement requirement)
    {
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is not null && await access.CanAuditAsync(id)) context.Succeed(requirement);
    }
}
