using System.Collections.Concurrent;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services;

public sealed class AccessConfigurationService(
    IDbContextFactory<ApplicationDbContext> factory,
    IServiceScopeFactory scopes,
    AuditService audit,
    ILogger<AccessConfigurationService>? logger = null,
    AppLogService? appLog = null,
    PermissionService? permissionService = null,
    IMemoryCache? cache = null)
{
    public const string CapabilityClaim = "querygate:capability";
    public const string AuditCapability = "audit";
    private readonly ConcurrentDictionary<string, byte> _auditCacheKeys = new(StringComparer.Ordinal);

    private static bool IsBuiltInProtectedRole(string? roleName) =>
        string.Equals(roleName, Roles.Admin, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(roleName, Roles.SuperAdmin, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> CanAuditAsync(string userId)
    {
        var cacheKey = "perm:canaudit:" + userId;
        if (cache != null && cache.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync();
        var allowed = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id
            join c in db.RoleClaims on r.Id equals c.RoleId into claims
            from c in claims.DefaultIfEmpty()
            where ur.UserId == userId && (r.NormalizedName == "ADMIN" || r.NormalizedName == "SUPERADMIN" ||
                (c != null && c.ClaimType == CapabilityClaim && c.ClaimValue == AuditCapability))
            select ur.UserId).AnyAsync();

        if (cache != null)
        {
            cache.Set(cacheKey, allowed, TimeSpan.FromMinutes(5));
            _auditCacheKeys.TryAdd(cacheKey, 0);
        }
        return allowed;
    }

    public void InvalidateAuditCache(string? userId = null)
    {
        if (cache == null) return;
        if (!string.IsNullOrEmpty(userId))
        {
            var key = "perm:canaudit:" + userId;
            cache.Remove(key);
            _auditCacheKeys.TryRemove(key, out _);
            return;
        }
        foreach (var k in _auditCacheKeys.Keys.ToList())
        {
            cache.Remove(k);
            _auditCacheKeys.TryRemove(k, out _);
        }
    }

    public async Task<OpResult> SaveRoleAsync(Actor actor, string id, string name, bool canAudit)
    {
        name = name.Trim();
        if (name.Length is 0 or > 256) return OpResult.Fail("Enter a role name of up to 256 characters.");
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var manager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                var role = await manager.FindByIdAsync(id);
                if (role is null) return OpResult.Fail("Role not found.");
                if (IsBuiltInProtectedRole(role.Name)) return OpResult.Fail("This built-in role is protected.");
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
                InvalidateAuditCache();
                permissionService?.Invalidate();
                await audit.TryWriteAsync(actor, "Permission", "RoleUpdated", "Success", $"{oldName} -> {name}; audit={canAudit}");
                return OpResult.Success();
            });
        }
        catch (Exception ex)
        {
            var msg = $"Failed to save role '{name}': {ex.InnerException?.Message ?? ex.Message}";
            logger?.LogError(ex, "Failed to save role {RoleId}", id);
            if (appLog != null)
            {
                await appLog.LogErrorAsync($"Failed to save role '{name}'", ex, actor?.UserName, "/admin/permissions", actor?.TenantId);
            }
            return OpResult.Fail(msg);
        }
    }

    public async Task<OpResult> DeleteRoleAsync(Actor actor, string id)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var manager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                var role = await manager.FindByIdAsync(id);
                if (role is null) return OpResult.Fail("Role not found.");
                if (IsBuiltInProtectedRole(role.Name)) return OpResult.Fail("This built-in role is protected.");
                if (await db.UserRoles.AnyAsync(x => x.RoleId == id) || await db.PermissionGrants.AnyAsync(x => x.RoleId == id) ||
                    await db.EnvironmentDefaultGrants.AnyAsync(x => x.RoleId == id))
                    return OpResult.Fail("Remove users, database grants and environment defaults from this role first.");
                var result = await manager.DeleteAsync(role);
                if (!result.Succeeded) return OpResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
                await tx.CommitAsync();
                InvalidateAuditCache();
                permissionService?.Invalidate();
                await audit.TryWriteAsync(actor, "Permission", "RoleDeleted", "Success", role.Name);
                return OpResult.Success();
            });
        }
        catch (Exception ex)
        {
            var msg = $"Failed to delete role: {ex.InnerException?.Message ?? ex.Message}";
            logger?.LogError(ex, "Failed to delete role {RoleId}", id);
            if (appLog != null)
            {
                await appLog.LogErrorAsync($"Failed to delete role '{id}'", ex, actor?.UserName, "/admin/permissions", actor?.TenantId);
            }
            return OpResult.Fail(msg);
        }
    }

    public async Task<OpResult> SaveEnvironmentAsync(Actor actor, Guid? id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 32) return OpResult.Fail("Enter an environment name of up to 32 characters.");
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var targetTenantId = actor?.TenantId ?? db.CurrentTenantId ?? TenantContextAccessor.FallbackTenantId;
            if (db.Tenant is null || !db.CurrentTenantId.HasValue)
            {
                var tc = new TenantContext();
                tc.SetTenant(targetTenantId, "default", "Default Workspace");
                db.Tenant = tc;
            }

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                if (await db.DatabaseEnvironments.AnyAsync(e => e.Name.ToLower() == name.ToLower() && e.Id != id))
                    return OpResult.Fail("Environment already exists.");

                var env = id is null ? null : await db.DatabaseEnvironments.FindAsync(id.Value);
                if (id is not null && env is null) return OpResult.Fail("Environment not found.");
                var old = env?.Name;
                if (env is null)
                {
                    db.DatabaseEnvironments.Add(new DatabaseEnvironment
                    {
                        Name = name,
                        TenantId = targetTenantId
                    });
                }
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
                permissionService?.Invalidate();
                await tx.CommitAsync();
                await audit.TryWriteAsync(actor, "Permission", "EnvironmentSaved", "Success", old is null ? name : $"{old} -> {name}");
                return OpResult.Success();
            });
        }
        catch (Exception ex)
        {
            var msg = $"Failed to save environment '{name}': {ex.InnerException?.Message ?? ex.Message}";
            logger?.LogError(ex, "Failed to save environment {Name}", name);
            if (appLog != null)
            {
                await appLog.LogErrorAsync($"Failed to save environment '{name}'", ex, actor?.UserName, "/admin/permissions", actor?.TenantId);
            }
            return OpResult.Fail(msg);
        }
    }

    public async Task<OpResult> DeleteEnvironmentAsync(Actor actor, Guid id)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var targetTenantId = actor?.TenantId ?? db.CurrentTenantId ?? TenantContextAccessor.FallbackTenantId;
            if (db.Tenant is null || !db.CurrentTenantId.HasValue)
            {
                var tc = new TenantContext();
                tc.SetTenant(targetTenantId, "default", "Default Workspace");
                db.Tenant = tc;
            }

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                var env = await db.DatabaseEnvironments.FindAsync(id);
                if (env is null) return OpResult.Fail("Environment not found.");
                if (await db.ManagedDatabases.AnyAsync(d => d.Environment == env.Name) ||
                    await db.EnvironmentDefaultGrants.AnyAsync(g => g.EnvironmentId == id))
                    return OpResult.Fail("Remove databases and default grants from this environment first.");
                db.DatabaseEnvironments.Remove(env);
                await db.SaveChangesAsync();
                permissionService?.Invalidate();
                await tx.CommitAsync();
                await audit.TryWriteAsync(actor, "Permission", "EnvironmentDeleted", "Success", env.Name);
                return OpResult.Success();
            });
        }
        catch (Exception ex)
        {
            var msg = $"Failed to delete environment: {ex.InnerException?.Message ?? ex.Message}";
            logger?.LogError(ex, "Failed to delete environment {EnvironmentId}", id);
            if (appLog != null)
            {
                await appLog.LogErrorAsync($"Failed to delete environment '{id}'", ex, actor?.UserName, "/admin/permissions", actor?.TenantId);
            }
            return OpResult.Fail(msg);
        }
    }

    public async Task<OpResult> SaveDefaultsAsync(Actor actor, Guid environmentId, string roleId, IEnumerable<string> permissions)
    {
        var codes = permissions.Select(c => c.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Any(c => c.Length > 128 || !Perm.IsValid(c))) return OpResult.Fail("Invalid permission.");
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var targetTenantId = actor?.TenantId ?? db.CurrentTenantId ?? TenantContextAccessor.FallbackTenantId;
            if (db.Tenant is null || !db.CurrentTenantId.HasValue)
            {
                var tc = new TenantContext();
                tc.SetTenant(targetTenantId, "default", "Default Workspace");
                db.Tenant = tc;
            }

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                if (!await db.DatabaseEnvironments.AnyAsync(e => e.Id == environmentId) || !await db.Roles.AnyAsync(r => r.Id == roleId))
                    return OpResult.Fail("Environment or role not found.");
                await db.EnvironmentDefaultGrants.Where(g => g.EnvironmentId == environmentId && g.RoleId == roleId).ExecuteDeleteAsync();
                db.EnvironmentDefaultGrants.AddRange(codes.Select(c => new EnvironmentDefaultGrant
                {
                    TenantId = targetTenantId,
                    EnvironmentId = environmentId,
                    RoleId = roleId,
                    PermissionCode = c
                }));
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                permissionService?.Invalidate();
                await audit.TryWriteAsync(actor, "Permission", "EnvironmentDefaultsUpdated", "Success", $"environment={environmentId}; role={roleId}; permissions={string.Join(",", codes)}");
                return OpResult.Success();
            });
        }
        catch (Exception ex)
        {
            var msg = $"Failed to save environment defaults: {ex.InnerException?.Message ?? ex.Message}";
            logger?.LogError(ex, "Failed to save environment defaults for {EnvironmentId}", environmentId);
            if (appLog != null)
            {
                await appLog.LogErrorAsync($"Failed to save environment defaults for {environmentId}", ex, actor?.UserName, "/admin/permissions", actor?.TenantId);
            }
            return OpResult.Fail(msg);
        }
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
                if (toRemove.Count > 0)
                {
                    db.PermissionGrants.RemoveRange(toRemove);
                    removedCount += toRemove.Count;
                    foreach (var rem in toRemove) existingGrants.Remove(rem);
                }
            }

            // 2. Synchronize target scopes and properties for selected permissions
            foreach (var code in finalCodes)
            {
                var matches = existingGrants
                    .Where(g => string.Equals(Perm.Normalize(g.PermissionCode), code, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var currentScopes = matches.Select(m => m.TargetScope).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Add missing scopes
                foreach (var s in parsedScopes)
                {
                    var existingMatch = matches.FirstOrDefault(m => string.Equals(m.TargetScope, s, StringComparison.OrdinalIgnoreCase));
                    if (existingMatch != null)
                    {
                        var changed = false;
                        if (existingMatch.Effect != effect) { existingMatch.Effect = effect; changed = true; }
                        if (existingMatch.StartsAt != startsAt) { existingMatch.StartsAt = startsAt; changed = true; }
                        if (existingMatch.EndsAt != endsAt) { existingMatch.EndsAt = endsAt; changed = true; }
                        if (changed) updatedCount++;
                    }
                    else
                    {
                        var (pSchema, pObj) = ObjectScope.Parse(s);
                        var newGrant = new PermissionGrant
                        {
                            DatabaseId = databaseId,
                            RoleId = cleanRoleId,
                            UserId = cleanUserId,
                            PermissionCode = code,
                            Effect = effect,
                            StartsAt = startsAt,
                            EndsAt = endsAt,
                            Schema = pSchema,
                            ObjectName = pObj
                        };
                        db.PermissionGrants.Add(newGrant);
                        existingGrants.Add(newGrant);
                        addedCount++;
                    }
                }

                // Remove scopes no longer configured for this permission
                var obsolete = matches.Where(m => !parsedScopes.Contains(m.TargetScope, StringComparer.OrdinalIgnoreCase)).ToList();
                foreach (var obs in obsolete)
                {
                    db.PermissionGrants.Remove(obs);
                    existingGrants.Remove(obs);
                    removedCount++;
                }
            }

            await db.SaveChangesAsync();
            permissionService?.Invalidate(userId: cleanUserId, databaseId: databaseId);

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
            if (appLog != null)
            {
                await appLog.LogErrorAsync($"Failed to save grants for database {databaseId}", ex, actor?.UserName, "/admin/permissions", actor?.TenantId);
            }
            return OpResult.Fail($"Failed to save grants: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
