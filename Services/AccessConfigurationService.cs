using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services;

public sealed class AccessConfigurationService(
    IDbContextFactory<ApplicationDbContext> factory,
    IServiceScopeFactory scopes,
    AuditService audit,
    ILogger<AccessConfigurationService>? logger = null)
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

    public async Task<OpResult> SaveGrantsAsync(
        Actor actor,
        Guid databaseId,
        string? roleId,
        string? userId,
        IEnumerable<string> finalPermissions,
        IEnumerable<string> initialPermissions,
        PermissionEffect effect,
        DateTime? starts = null,
        DateTime? ends = null,
        string? targetScope = null,
        IEnumerable<string>? targetScopes = null)
    {
        try
        {
            var rawScopes = targetScopes != null ? targetScopes.ToList() : (targetScope != null ? [targetScope] : null);
            var parsedScopes = ObjectScope.ParseList(rawScopes);

            var cleanRoleId = string.IsNullOrWhiteSpace(roleId) ? null : roleId.Trim();
            var cleanUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();

            if (cleanRoleId == null && cleanUserId == null)
                return OpResult.Fail("Select either a role or a user.");
            if (cleanRoleId != null && cleanUserId != null)
                return OpResult.Fail("Cannot assign grant to both a role and a user simultaneously.");

            var finalCodes = finalPermissions
                .Select(Perm.Normalize)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (finalCodes.Any(c => c.Length > 128 || !Perm.IsValid(c)))
                return OpResult.Fail("Invalid permission code.");

            if (starts is not null && ends is not null && ends <= starts)
                return OpResult.Fail("End time must be after start time.");

            await using var db = await factory.CreateDbContextAsync();
            var targetDb = await db.ManagedDatabases.FindAsync(databaseId);
            if (targetDb is null) return OpResult.Fail("Target database was not found.");

            var startsAt = starts.HasValue ? new DateTimeOffset(starts.Value, TimeZoneInfo.Local.GetUtcOffset(starts.Value)) : (DateTimeOffset?)null;
            var endsAt = ends.HasValue ? new DateTimeOffset(ends.Value, TimeZoneInfo.Local.GetUtcOffset(ends.Value)) : (DateTimeOffset?)null;

            var q = db.PermissionGrants.Where(g => g.DatabaseId == databaseId);
            if (cleanRoleId != null)
            {
                q = q.Where(g => g.RoleId == cleanRoleId);
            }
            else
            {
                q = q.Where(g => g.UserId == cleanUserId);
            }
            var existingGrants = await q.ToListAsync();

            var removedCount = 0;
            var updatedCount = 0;
            var addedCount = 0;

            // 1. Remove unchecked permissions completely
            var initialSet = initialPermissions.Select(Perm.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var uncheckedCodes = initialSet.Where(c => !finalCodes.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var code in uncheckedCodes)
            {
                var toRemove = existingGrants.Where(g => string.Equals(Perm.Normalize(g.PermissionCode), code, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var g in toRemove)
                {
                    db.PermissionGrants.Remove(g);
                    existingGrants.Remove(g);
                    removedCount++;
                }
            }

            // 2. For finalCodes, synchronize target scopes
            foreach (var code in finalCodes)
            {
                var currentForCode = existingGrants
                    .Where(g => string.Equals(Perm.Normalize(g.PermissionCode), code, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var scopesToKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var scopeStr in parsedScopes)
                {
                    var (schema, obj) = ObjectScope.Parse(scopeStr);
                    scopesToKeep.Add(ObjectScope.Format(schema, obj));

                    var existing = currentForCode.FirstOrDefault(g =>
                        string.Equals(g.Schema, schema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(g.ObjectName, obj, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        existing.Effect = effect;
                        existing.StartsAt = startsAt;
                        existing.EndsAt = endsAt;
                        updatedCount++;
                    }
                    else
                    {
                        db.PermissionGrants.Add(new PermissionGrant
                        {
                            DatabaseId = databaseId,
                            PermissionCode = code,
                            Schema = schema,
                            ObjectName = obj,
                            Effect = effect,
                            UserId = cleanUserId,
                            RoleId = cleanRoleId,
                            StartsAt = startsAt,
                            EndsAt = endsAt
                        });
                        addedCount++;
                    }
                }

                // Remove any existing grants for this code whose scope is no longer in parsedScopes
                var obsoleteGrants = currentForCode
                    .Where(g => !scopesToKeep.Contains(ObjectScope.Format(g.Schema, g.ObjectName)))
                    .ToList();

                foreach (var obs in obsoleteGrants)
                {
                    db.PermissionGrants.Remove(obs);
                    existingGrants.Remove(obs);
                    removedCount++;
                }
            }

            await db.SaveChangesAsync();

            var subjectKind = cleanRoleId != null ? "role" : "user";
            var subjectId = cleanRoleId ?? cleanUserId;
            var scopeSummary = string.Join(", ", parsedScopes);
            await audit.TryWriteAsync(actor, "Permission", "GrantsSaved", "Success",
                $"Applied grants for {subjectKind} {subjectId} on {targetDb.Name} (scopes: {scopeSummary}): +{addedCount}, ~{updatedCount}, -{removedCount}",
                databaseId, targetDb.Name);

            return OpResult.Success($"{addedCount}:{updatedCount}:{removedCount}");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to save grants for database {DatabaseId}", databaseId);
            return OpResult.Fail($"Failed to save grants: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
