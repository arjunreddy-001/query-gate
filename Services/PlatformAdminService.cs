using System.Text.RegularExpressions;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed record PlatformOperatorInfo(
    string UserId,
    string Email,
    string? DisplayName,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset? LockoutEnd,
    int WorkspaceCount);

public sealed record WorkspaceMemberInfo(
    string UserId,
    string Email,
    string? DisplayName,
    string WorkspaceRole,
    bool IsActive,
    bool IsSuperAdmin,
    DateTimeOffset JoinedAt);

public sealed partial class PlatformAdminService(
    IDbContextFactory<ApplicationDbContext> factory,
    IServiceScopeFactory scopes,
    AuditService audit,
    ITenantLookupCache tenantCache,
    SettingsService settings,
    IUserStatusCache? userStatusCache = null,
    PermissionService? permissions = null)
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SlugPattern();

    private static Actor PlatformActor(Actor actor) =>
        actor with { TenantId = WellKnownTenants.Platform };

    public async Task<List<PlatformOperatorInfo>> ListOperatorsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var superRoleId = await db.Roles.AsNoTracking()
            .Where(r => r.Name == Roles.SuperAdmin)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();
        if (superRoleId is null) return [];

        var opUsers = await (from ur in db.UserRoles.AsNoTracking()
                             join u in db.Users.AsNoTracking() on ur.UserId equals u.Id
                             where ur.RoleId == superRoleId
                             orderby u.Email
                             select u).ToListAsync();

        var userIds = opUsers.Select(u => u.Id).ToList();
        var membershipCounts = await db.UserTenantMemberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => userIds.Contains(m.UserId))
            .GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        return opUsers.Select(u => new PlatformOperatorInfo(
            u.Id,
            u.Email ?? u.UserName ?? "",
            u.DisplayName,
            u.IsActive,
            u.MustChangePassword,
            u.LockoutEnd,
            membershipCounts.GetValueOrDefault(u.Id, 0))).ToList();
    }

    public async Task<OpResult> GrantSuperAdminAsync(Actor actor, string email, string? displayName = null)
    {
        var trimmedEmail = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedEmail) || !trimmedEmail.Contains('@'))
            return OpResult.Fail("Enter a valid email address.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roles.RoleExistsAsync(Roles.SuperAdmin))
        {
            await roles.CreateAsync(new IdentityRole(Roles.SuperAdmin));
        }

        var existing = await users.FindByEmailAsync(trimmedEmail);
        string? generatedPassword = null;
        ApplicationUser user;

        if (existing is not null)
        {
            if (await users.IsInRoleAsync(existing, Roles.SuperAdmin))
                return OpResult.Fail("User is already a SuperAdmin.");

            user = existing;
            var addRes = await users.AddToRoleAsync(user, Roles.SuperAdmin);
            if (!addRes.Succeeded)
                return OpResult.Fail(string.Join(" ", addRes.Errors.Select(e => e.Description)));
            await users.UpdateSecurityStampAsync(user);
        }
        else
        {
            generatedPassword = UserAdminService.GeneratePassword();
            var finalName = string.IsNullOrWhiteSpace(displayName) ? trimmedEmail.Split('@')[0] : displayName.Trim();
            user = new ApplicationUser
            {
                UserName = trimmedEmail,
                Email = trimmedEmail,
                EmailConfirmed = true,
                DisplayName = finalName,
                IsActive = true,
                MustChangePassword = true,
                CreatedBy = actor.UserName
            };
            var createRes = await users.CreateAsync(user, generatedPassword);
            if (!createRes.Succeeded)
                return OpResult.Fail(string.Join(" ", createRes.Errors.Select(e => e.Description)));

            var addRes = await users.AddToRoleAsync(user, Roles.SuperAdmin);
            if (!addRes.Succeeded)
                return OpResult.Fail(string.Join(" ", addRes.Errors.Select(e => e.Description)));
        }

        userStatusCache?.Invalidate(user.Id);
        permissions?.Invalidate(userId: user.Id);
        await audit.TryWriteAsync(
            PlatformActor(actor),
            "Platform",
            "GrantSuperAdmin",
            "Success",
            $"{trimmedEmail} (mode={(generatedPassword is null ? "promoted" : "created")})");

        return OpResult.Success(generatedPassword);
    }

    public async Task<OpResult> RevokeSuperAdminAsync(Actor actor, string targetUserId)
    {
        if (string.IsNullOrWhiteSpace(targetUserId))
            return OpResult.Fail("User not found.");
        if (targetUserId == actor.UserId)
            return OpResult.Fail("You cannot revoke your own SuperAdmin role.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var target = await users.FindByIdAsync(targetUserId);
        if (target is null) return OpResult.Fail("User not found.");
        if (!await users.IsInRoleAsync(target, Roles.SuperAdmin))
            return OpResult.Fail("User does not hold the SuperAdmin role.");

        var activeSuperAdmins = await (from ur in db.UserRoles
                                       join r in db.Roles on ur.RoleId equals r.Id
                                       join u in db.Users on ur.UserId equals u.Id
                                       where r.Name == Roles.SuperAdmin && u.IsActive
                                       select u.Id).Distinct().CountAsync();

        if (target.IsActive && activeSuperAdmins <= 1)
            return OpResult.Fail("Cannot revoke the last active SuperAdmin.");

        var remRes = await users.RemoveFromRoleAsync(target, Roles.SuperAdmin);
        if (!remRes.Succeeded)
            return OpResult.Fail(string.Join(" ", remRes.Errors.Select(e => e.Description)));

        await users.UpdateSecurityStampAsync(target);
        await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == targetUserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));

        userStatusCache?.Invalidate(targetUserId);
        permissions?.Invalidate(userId: targetUserId);
        await audit.TryWriteAsync(PlatformActor(actor), "Platform", "RevokeSuperAdmin", "Success", target.Email ?? targetUserId);
        return OpResult.Success();
    }

    public async Task<OpResult> CreateWorkspaceAsync(
        Actor actor,
        string name,
        string slug,
        string? contactEmail,
        string? plan,
        int? maxDatabases,
        int? maxUsers,
        string? initialAdminEmail)
    {
        if (string.IsNullOrWhiteSpace(name))
            return OpResult.Fail("Workspace name is required.");

        var cleanSlug = (slug ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanSlug) || !SlugPattern().IsMatch(cleanSlug))
            return OpResult.Fail("Slug must contain only lowercase letters, numbers, and hyphens.");
        if (cleanSlug == "platform")
            return OpResult.Fail("The slug 'platform' is reserved.");

        if (maxDatabases is <= 0 || maxUsers is <= 0)
            return OpResult.Fail("Quotas must be positive numbers or left blank for unlimited.");

        await using var db = await factory.CreateDbContextAsync();
        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == cleanSlug))
            return OpResult.Fail("A workspace with that slug already exists.");

        var tenant = new Tenant
        {
            Name = name.Trim(),
            Slug = cleanSlug,
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim(),
            Plan = string.IsNullOrWhiteSpace(plan) ? null : plan.Trim(),
            MaxDatabases = maxDatabases,
            MaxUsers = maxUsers,
            IsActive = true
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        string? adminNote = null;
        if (!string.IsNullOrWhiteSpace(initialAdminEmail))
        {
            var trimmedAdmin = initialAdminEmail.Trim();
            var normalized = trimmedAdmin.ToUpperInvariant();
            var adminUser = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized || u.Email == trimmedAdmin);
            if (adminUser is not null)
            {
                db.UserTenantMemberships.Add(new UserTenantMembership
                {
                    UserId = adminUser.Id,
                    TenantId = tenant.Id,
                    Role = Roles.Admin
                });
                await db.SaveChangesAsync();
                await EnsureUserHasRoleAsync(adminUser.Id, Roles.Admin);
                adminNote = $"Assigned {adminUser.Email} as Admin.";
            }
            else
            {
                adminNote = $"No existing account matched '{trimmedAdmin}'. Assign an admin from Members.";
            }
        }

        tenantCache.Invalidate();
        await audit.TryWriteAsync(
            PlatformActor(actor),
            "Platform",
            "CreateWorkspace",
            "Success",
            $"name={tenant.Name}; slug={tenant.Slug}; maxDb={maxDatabases?.ToString() ?? "unlimited"}; maxUsers={maxUsers?.ToString() ?? "unlimited"}");

        return OpResult.Success(adminNote);
    }

    public async Task<OpResult> UpdateWorkspaceAsync(
        Actor actor,
        Guid tenantId,
        string name,
        string? contactEmail,
        string? plan,
        int? maxDatabases,
        int? maxUsers)
    {
        if (string.IsNullOrWhiteSpace(name))
            return OpResult.Fail("Workspace name is required.");
        if (maxDatabases is <= 0 || maxUsers is <= 0)
            return OpResult.Fail("Quotas must be positive numbers or left blank for unlimited.");

        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (existing is null) return OpResult.Fail("Workspace not found.");

        existing.Name = name.Trim();
        existing.ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim();
        existing.Plan = string.IsNullOrWhiteSpace(plan) ? null : plan.Trim();
        existing.MaxDatabases = maxDatabases;
        existing.MaxUsers = maxUsers;
        await db.SaveChangesAsync();

        tenantCache.Invalidate();
        await audit.TryWriteAsync(
            PlatformActor(actor),
            "Platform",
            "UpdateWorkspace",
            "Success",
            $"slug={existing.Slug}; name={existing.Name}; maxDb={maxDatabases?.ToString() ?? "unlimited"}; maxUsers={maxUsers?.ToString() ?? "unlimited"}");
        return OpResult.Success();
    }

    public async Task<OpResult> ToggleWorkspaceActiveAsync(Actor actor, Guid tenantId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (existing is null) return OpResult.Fail("Workspace not found.");

        existing.IsActive = !existing.IsActive;
        await db.SaveChangesAsync();

        tenantCache.Invalidate();
        await audit.TryWriteAsync(
            PlatformActor(actor),
            "Platform",
            existing.IsActive ? "EnableWorkspace" : "DisableWorkspace",
            "Success",
            $"slug={existing.Slug}");
        return OpResult.Success(existing.IsActive ? "enabled" : "disabled");
    }

    public async Task<OpResult> DeleteWorkspaceAsync(Actor actor, Guid tenantId, string confirmSlug)
    {
        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant is null) return OpResult.Fail("Workspace not found.");
        if (string.Equals(tenant.Slug, "default", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("The default workspace cannot be deleted.");
        if (tenant.IsActive)
            return OpResult.Fail("Disable the workspace before deleting it.");
        if (!string.Equals((confirmSlug ?? "").Trim(), tenant.Slug, StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("Confirmation slug does not match.");

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();

            // Delete tenant-scoped operational rows in FK-safe order while preserving AuditLogs for compliance.
            await db.PermissionGrants.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.DatabaseRoleScopeRules.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.EnvironmentDefaultGrants.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ManagedDatabases.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.DatabaseEnvironments.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.RequestStatusHistory.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AccessRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.ScriptRequests.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.SavedQueries.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.QueryHistory.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Notifications.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Settings.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.RefreshTokens.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.AppLogs.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.TenantDomains.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.TenantRoles.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.UserTenantMemberships.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync();
            await db.Tenants.IgnoreQueryFilters().Where(x => x.Id == tenantId).ExecuteDeleteAsync();

            await tx.CommitAsync();
            tenantCache.Invalidate();
            permissions?.Invalidate(tenantId: tenantId);
            await audit.TryWriteAsync(PlatformActor(actor), "Platform", "DeleteWorkspace", "Success", $"name={tenant.Name}; slug={tenant.Slug}");
            return OpResult.Success();
        });
    }

    public async Task<OpResult> AddDomainAsync(Actor actor, Guid tenantId, string rawDomain)
    {
        var domain = (rawDomain ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain) || domain.Contains('/') || domain.Contains(':'))
            return OpResult.Fail("Enter a valid hostname only (e.g. data.company.com). Do not include http:// or ports.");

        if (await settings.GetPlatformBoolAsync("Platform:EnforceDomainAllowlist"))
        {
            var suffixes = (await settings.GetPlatformAsync("Platform:AllowedDomainSuffixes"))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (suffixes.Length > 0 && !suffixes.Any(s => domain.EndsWith(s.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)))
                return OpResult.Fail($"Domain must end with an allowed suffix ({string.Join(", ", suffixes)}).");
        }

        await using var db = await factory.CreateDbContextAsync();
        if (await db.TenantDomains.IgnoreQueryFilters().AnyAsync(d => d.Domain == domain))
            return OpResult.Fail("That domain is already mapped to a workspace.");

        var tenant = await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant is null) return OpResult.Fail("Workspace not found.");

        db.TenantDomains.Add(new TenantDomain
        {
            TenantId = tenantId,
            Domain = domain,
            IsVerified = true
        });
        await db.SaveChangesAsync();
        tenantCache.Invalidate();
        await audit.TryWriteAsync(PlatformActor(actor), "Platform", "AddDomain", "Success", $"{domain} -> {tenant.Slug}");
        return OpResult.Success();
    }

    public async Task<OpResult> RemoveDomainAsync(Actor actor, Guid tenantId, Guid domainId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var item = await db.TenantDomains.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == domainId && d.TenantId == tenantId);
        if (item is null) return OpResult.Fail("Domain not found.");

        var domain = item.Domain;
        db.TenantDomains.Remove(item);
        await db.SaveChangesAsync();
        tenantCache.Invalidate();
        await audit.TryWriteAsync(PlatformActor(actor), "Platform", "RemoveDomain", "Success", domain);
        return OpResult.Success();
    }

    public async Task<List<WorkspaceMemberInfo>> ListWorkspaceMembersAsync(Guid tenantId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var memberships = await db.UserTenantMemberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync();

        if (memberships.Count == 0) return [];
        var userIds = memberships.Select(m => m.UserId).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        var superRoleId = await db.Roles.AsNoTracking()
            .Where(r => r.Name == Roles.SuperAdmin)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();
        var superIds = superRoleId is null
            ? new HashSet<string>()
            : (await db.UserRoles.AsNoTracking()
                .Where(ur => ur.RoleId == superRoleId && userIds.Contains(ur.UserId))
                .Select(ur => ur.UserId)
                .ToListAsync()).ToHashSet();

        return memberships
            .Where(m => users.ContainsKey(m.UserId))
            .Select(m =>
            {
                var u = users[m.UserId];
                return new WorkspaceMemberInfo(u.Id, u.Email ?? u.UserName ?? "", u.DisplayName, m.Role, u.IsActive, superIds.Contains(u.Id), m.JoinedAt);
            })
            .OrderBy(x => x.Email)
            .ToList();
    }

    public async Task<OpResult> AssignWorkspaceMemberAsync(Actor actor, Guid tenantId, string email, string role)
    {
        var trimmedEmail = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedEmail) || !trimmedEmail.Contains('@'))
            return OpResult.Fail("Enter a valid email address.");
        if (string.Equals(role, Roles.SuperAdmin, StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("Use Platform Operators to grant SuperAdmin.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(role))
            return OpResult.Fail("Role not found.");

        await using var db = await factory.CreateDbContextAsync();
        var tenant = await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant is null) return OpResult.Fail("Workspace not found.");

        var user = await users.FindByEmailAsync(trimmedEmail);
        string? generatedPassword = null;

        if (user is null)
        {
            if (tenant.MaxUsers is int maxUsers && maxUsers > 0)
            {
                var currentCount = await db.UserTenantMemberships.IgnoreQueryFilters().CountAsync(m => m.TenantId == tenantId);
                if (currentCount >= maxUsers)
                    return OpResult.Fail($"Workspace user limit ({maxUsers}) reached.");
            }

            generatedPassword = UserAdminService.GeneratePassword();
            user = new ApplicationUser
            {
                UserName = trimmedEmail,
                Email = trimmedEmail,
                EmailConfirmed = true,
                DisplayName = trimmedEmail.Split('@')[0],
                IsActive = true,
                MustChangePassword = true,
                CreatedBy = actor.UserName
            };
            var createRes = await users.CreateAsync(user, generatedPassword);
            if (!createRes.Succeeded)
                return OpResult.Fail(string.Join(" ", createRes.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, role);
        }

        var existingMembership = await db.UserTenantMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == user.Id && m.TenantId == tenantId);

        if (existingMembership is null)
        {
            if (tenant.MaxUsers is int maxUsers && maxUsers > 0)
            {
                var currentCount = await db.UserTenantMemberships.IgnoreQueryFilters().CountAsync(m => m.TenantId == tenantId);
                if (currentCount >= maxUsers)
                    return OpResult.Fail($"Workspace user limit ({maxUsers}) reached.");
            }

            db.UserTenantMemberships.Add(new UserTenantMembership
            {
                UserId = user.Id,
                TenantId = tenantId,
                Role = role
            });
            await db.SaveChangesAsync();
            if (!await users.IsInRoleAsync(user, role))
            {
                await users.AddToRoleAsync(user, role);
            }
        }
        else
        {
            existingMembership.Role = role;
            await db.SaveChangesAsync();
            var currentRoles = await users.GetRolesAsync(user);
            var toRemove = currentRoles.Where(r => !string.Equals(r, Roles.SuperAdmin, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (toRemove.Length > 0) await users.RemoveFromRolesAsync(user, toRemove);
            if (!currentRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                await users.AddToRoleAsync(user, role);
            await users.UpdateSecurityStampAsync(user);
        }

        userStatusCache?.Invalidate(user.Id);
        permissions?.Invalidate(userId: user.Id, tenantId: tenantId);
        await audit.TryWriteAsync(PlatformActor(actor), "Platform", "AssignWorkspaceMember", "Success", $"{trimmedEmail} -> {tenant.Slug} ({role})");
        return OpResult.Success(generatedPassword);
    }

    public async Task<OpResult> RemoveWorkspaceMemberAsync(Actor actor, Guid tenantId, string userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var membership = await db.UserTenantMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId);
        if (membership is null) return OpResult.Fail("Membership not found.");

        var tenant = await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId);
        var userEmail = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();

        db.UserTenantMemberships.Remove(membership);
        await db.SaveChangesAsync();
        permissions?.Invalidate(userId: userId, tenantId: tenantId);
        await audit.TryWriteAsync(PlatformActor(actor), "Platform", "RemoveWorkspaceMember", "Success", $"{userEmail ?? userId} from {tenant?.Slug ?? tenantId.ToString()}");
        return OpResult.Success();
    }

    private async Task EnsureUserHasRoleAsync(string userId, string role)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is not null && !await users.IsInRoleAsync(user, role))
        {
            await users.AddToRoleAsync(user, role);
        }
    }
}
