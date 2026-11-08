using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed class AccessConfigurationService(IDbContextFactory<ApplicationDbContext> factory, IServiceScopeFactory scopes, AuditService audit)
{
    public const string CapabilityClaim = "querygate:capability";
    public const string AuditCapability = "audit";

    public async Task<bool> CanAuditAsync(string userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id
            join c in db.RoleClaims on r.Id equals c.RoleId into claims
            from c in claims.DefaultIfEmpty()
            where ur.UserId == userId && (r.NormalizedName == "ADMIN" ||
                (c != null && c.ClaimType == CapabilityClaim && c.ClaimValue == AuditCapability))
            select ur.UserId).AnyAsync();
    }

    public async Task<OpResult> SaveRoleAsync(Actor actor, string id, string name, bool canAudit)
    {
        name = name.Trim();
        if (name.Length is 0 or > 256) return OpResult.Fail("Enter a role name of up to 256 characters.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var role = await manager.FindByIdAsync(id);
        if (role is null) return OpResult.Fail("Role not found.");
        if (string.Equals(role.Name, Roles.Admin, StringComparison.OrdinalIgnoreCase)) return OpResult.Fail("The Admin role is protected.");
        var existing = await manager.FindByNameAsync(name);
        if (existing is not null && existing.Id != id) return OpResult.Fail("Role already exists.");
        var oldName = role.Name ?? "";
        var renamed = await manager.SetRoleNameAsync(role, name);
        if (!renamed.Succeeded) return OpResult.Fail(string.Join(" ", renamed.Errors.Select(e => e.Description)));
        var updated = await manager.UpdateAsync(role);
        if (!updated.Succeeded) return OpResult.Fail(string.Join(" ", updated.Errors.Select(e => e.Description)));
        var claims = await manager.GetClaimsAsync(role);
        foreach (var claim in claims.Where(c => c.Type == CapabilityClaim && c.Value == AuditCapability))
        {
            var removed = await manager.RemoveClaimAsync(role, claim);
            if (!removed.Succeeded) return OpResult.Fail(string.Join(" ", removed.Errors.Select(e => e.Description)));
        }
        if (canAudit)
        {
            var added = await manager.AddClaimAsync(role, new System.Security.Claims.Claim(CapabilityClaim, AuditCapability));
            if (!added.Succeeded) return OpResult.Fail(string.Join(" ", added.Errors.Select(e => e.Description)));
        }
        var securityStamp = Guid.NewGuid().ToString();
        await db.Users.Where(u => db.UserRoles.Any(ur => ur.RoleId == id && ur.UserId == u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, securityStamp));
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "Permission", "RoleUpdated", "Success", $"{oldName} -> {name}; audit={canAudit}");
        return OpResult.Success();
    }

    public async Task<OpResult> DeleteRoleAsync(Actor actor, string id)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var manager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var role = await manager.FindByIdAsync(id);
        if (role is null) return OpResult.Fail("Role not found.");
        if (string.Equals(role.Name, Roles.Admin, StringComparison.OrdinalIgnoreCase)) return OpResult.Fail("The Admin role is protected.");
        if (await db.UserRoles.AnyAsync(x => x.RoleId == id) || await db.PermissionGrants.AnyAsync(x => x.RoleId == id) ||
            await db.EnvironmentDefaultGrants.AnyAsync(x => x.RoleId == id))
            return OpResult.Fail("Remove users, database grants and environment defaults from this role first.");
        var result = await manager.DeleteAsync(role);
        if (!result.Succeeded) return OpResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "Permission", "RoleDeleted", "Success", role.Name);
        return OpResult.Success();
    }

    public async Task<OpResult> SaveEnvironmentAsync(Actor actor, Guid? id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 32) return OpResult.Fail("Enter an environment name of up to 32 characters.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (await db.DatabaseEnvironments.AnyAsync(e => e.Name == name && e.Id != id)) return OpResult.Fail("Environment already exists.");
        var env = id is null ? null : await db.DatabaseEnvironments.FindAsync(id.Value);
        if (id is not null && env is null) return OpResult.Fail("Environment not found.");
        var old = env?.Name;
        if (env is null) db.DatabaseEnvironments.Add(new DatabaseEnvironment { Name = name });
        else
        {
            if (await (from source in db.ManagedDatabases
                join target in db.ManagedDatabases on source.Name equals target.Name
                where source.Environment == old && target.Environment == name && source.Id != target.Id
                select source.Id).AnyAsync())
                return OpResult.Fail("A database with the same name already exists in the target environment.");
            env.Name = name;
            await db.ManagedDatabases.Where(d => d.Environment == old)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Environment, name));
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "Permission", "EnvironmentSaved", "Success", old is null ? name : $"{old} -> {name}");
        return OpResult.Success();
    }

    public async Task<OpResult> DeleteEnvironmentAsync(Actor actor, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var env = await db.DatabaseEnvironments.FindAsync(id);
        if (env is null) return OpResult.Fail("Environment not found.");
        if (await db.ManagedDatabases.AnyAsync(d => d.Environment == env.Name) ||
            await db.EnvironmentDefaultGrants.AnyAsync(g => g.EnvironmentId == id))
            return OpResult.Fail("Remove databases and default grants from this environment first.");
        db.DatabaseEnvironments.Remove(env);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "Permission", "EnvironmentDeleted", "Success", env.Name);
        return OpResult.Success();
    }

    public async Task<OpResult> SaveDefaultsAsync(Actor actor, Guid environmentId, string roleId, IEnumerable<string> permissions)
    {
        var codes = permissions.Select(c => c.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Any(c => c.Length > 128 || !Perm.IsValid(c))) return OpResult.Fail("Invalid permission.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (!await db.DatabaseEnvironments.AnyAsync(e => e.Id == environmentId) || !await db.Roles.AnyAsync(r => r.Id == roleId))
            return OpResult.Fail("Environment or role not found.");
        await db.EnvironmentDefaultGrants.Where(g => g.EnvironmentId == environmentId && g.RoleId == roleId).ExecuteDeleteAsync();
        db.EnvironmentDefaultGrants.AddRange(codes.Select(c => new EnvironmentDefaultGrant { EnvironmentId = environmentId, RoleId = roleId, PermissionCode = c }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "Permission", "EnvironmentDefaultsUpdated", "Success", $"environment={environmentId}; role={roleId}; permissions={string.Join(",", codes)}");
        return OpResult.Success();
    }
}
