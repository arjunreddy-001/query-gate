using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class SuperAdminAndPlatformTests
{
    private sealed class TenantTestFactory(DbContextOptions<ApplicationDbContext> options, ITenantContext tenantContext)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, tenantContext);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options, tenantContext));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public SqliteConnection Connection { get; }
        public TenantContext TenantCtx { get; } = new();
        public IDbContextFactory<ApplicationDbContext> Factory { get; }
        public IServiceScopeFactory ScopeFactory { get; }
        public CryptoService Crypto { get; }
        public SettingsService Settings { get; }
        public AuditService Audit { get; }
        public PermissionService Permissions { get; }
        public TenantLookupCache TenantCache { get; } = new();
        public UserStatusCache UserCache { get; }
        public DatabaseCache DbCache { get; }
        public UserAdminService UserAdmin { get; }
        public DatabaseAdminService DatabaseAdmin { get; }
        public PlatformAdminService PlatformAdmin { get; }
        public AccessConfigurationService AccessConfig { get; }
        public ExecutionService Execution { get; }
        public Guid DefaultTenantId { get; }

        public Harness()
        {
            Connection = new SqliteConnection("Data Source=:memory:");
            Connection.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Connection)
                .Options;

            using (var init = new ApplicationDbContext(options))
            {
                init.Database.EnsureCreated();
            }

            Factory = new TenantTestFactory(options, TenantCtx);
            var memCache = new MemoryCache(new MemoryCacheOptions());
            var tenantAccessor = new TenantContextAccessor { Current = TenantCtx };
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = Convert.ToBase64String(new byte[32]) })
                .Build();

            Crypto = new CryptoService(config);
            Settings = new SettingsService(Factory, memCache, tenantAccessor);
            Audit = new AuditService(Factory, NullLogger<AuditService>.Instance);
            Permissions = new PermissionService(Factory, memCache);
            UserCache = new UserStatusCache(memCache);
            DbCache = new DatabaseCache(Factory, Crypto, memCache);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Factory);
            services.AddScoped(_ => Factory.CreateDbContext());
            services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = true;
            })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();
            ScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var providers = new DatabaseProviderFactory();
            var gate = new SqlGate(memCache);
            Execution = new ExecutionService(Factory, ScopeFactory, gate, Permissions, Audit, Crypto, Settings, NullLogger<ExecutionService>.Instance, providers, DbCache, UserCache);
            UserAdmin = new UserAdminService(Factory, ScopeFactory, Audit, Execution, UserCache, Permissions);
            DatabaseAdmin = new DatabaseAdminService(Factory, Crypto, Audit, providers, DbCache, Permissions);
            PlatformAdmin = new PlatformAdminService(Factory, ScopeFactory, Audit, TenantCache, Settings, UserCache, Permissions);
            AccessConfig = new AccessConfigurationService(Factory, ScopeFactory, Audit, null, null, Permissions, memCache);

            using var scope = ScopeFactory.CreateScope();
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var r in new[] { Roles.SuperAdmin, Roles.Admin, Roles.Developer, Roles.Tester, Roles.Auditor })
            {
                roleMgr.CreateAsync(new IdentityRole(r)).GetAwaiter().GetResult();
            }

            using var db = Factory.CreateDbContext();
            var tenant = new Tenant { Name = "Default Workspace", Slug = "default", IsActive = true };
            db.Tenants.Add(tenant);
            db.SaveChanges();
            DefaultTenantId = tenant.Id;
            TenantCtx.SetTenant(tenant.Id, tenant.Slug, tenant.Name);
        }

        public async Task<ApplicationUser> CreateUserAsync(string id, string email, params string[] roles)
        {
            using var scope = ScopeFactory.CreateScope();
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = id,
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = email,
                IsActive = true
            };
            var res = await userMgr.CreateAsync(user, "ValidPass!1234");
            Assert.True(res.Succeeded, string.Join(", ", res.Errors.Select(e => e.Description)));
            foreach (var role in roles)
            {
                await userMgr.AddToRoleAsync(user, role);
            }
            return user;
        }

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }

    [Fact]
    public async Task UserAdminService_BlocksWorkspaceAdminFromAssigningOrModifyingSuperAdmin()
    {
        await using var h = new Harness();
        var workspaceAdmin = await h.CreateUserAsync("admin-1", "admin@acme.test", Roles.Admin);
        var superAdmin = await h.CreateUserAsync("super-1", "super@acme.test", Roles.SuperAdmin, Roles.Admin);
        var dev = await h.CreateUserAsync("dev-1", "dev@acme.test", Roles.Developer);

        var adminActor = new Actor(workspaceAdmin.Id, workspaceAdmin.Email!, "127.0.0.1", "Test", h.DefaultTenantId);

        // Cannot create a user with SuperAdmin role
        var createSuper = await h.UserAdmin.CreateAsync(adminActor, "newop@acme.test", "New Op", Roles.SuperAdmin, null);
        Assert.False(createSuper.Ok);

        // Cannot promote a developer to SuperAdmin via SetRoleAsync
        var promote = await h.UserAdmin.SetRoleAsync(adminActor, dev.Id, Roles.SuperAdmin);
        Assert.False(promote.Ok);

        // Workspace Admin cannot modify an existing SuperAdmin account
        Assert.False((await h.UserAdmin.SetRoleAsync(adminActor, superAdmin.Id, Roles.Developer)).Ok);
        Assert.False((await h.UserAdmin.SetActiveAsync(adminActor, superAdmin.Id, false)).Ok);
        Assert.False((await h.UserAdmin.ResetPasswordAsync(adminActor, superAdmin.Id)).Ok);
        Assert.False((await h.UserAdmin.SetExpiryAsync(adminActor, superAdmin.Id, DateTimeOffset.UtcNow.AddDays(5), null)).Ok);
    }

    [Fact]
    public async Task UserAdminService_SetRoleAsync_PreservesSuperAdminAndSyncsMembership()
    {
        await using var h = new Harness();
        var actorSuper = await h.CreateUserAsync("super-actor", "super1@acme.test", Roles.SuperAdmin);
        var targetSuper = await h.CreateUserAsync("super-target", "super2@acme.test", Roles.SuperAdmin, Roles.Admin);

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.UserTenantMemberships.Add(new UserTenantMembership
            {
                UserId = targetSuper.Id,
                TenantId = h.DefaultTenantId,
                Role = Roles.Admin
            });
            await db.SaveChangesAsync();
        }

        var superActor = new Actor(actorSuper.Id, actorSuper.Email!, "127.0.0.1", "Test", h.DefaultTenantId);
        var res = await h.UserAdmin.SetRoleAsync(superActor, targetSuper.Id, Roles.Developer);
        Assert.True(res.Ok, res.Error);

        using var scope = h.ScopeFactory.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var refreshed = await userMgr.FindByIdAsync(targetSuper.Id);
        var roles = await userMgr.GetRolesAsync(refreshed!);
        Assert.Contains(Roles.SuperAdmin, roles);
        Assert.Contains(Roles.Developer, roles);
        Assert.DoesNotContain(Roles.Admin, roles);

        await using var verifyDb = await h.Factory.CreateDbContextAsync();
        var membership = await verifyDb.UserTenantMemberships.FirstAsync(m => m.UserId == targetSuper.Id);
        Assert.Equal(Roles.Developer, membership.Role);
    }

    [Fact]
    public async Task PlatformAdminService_GrantAndRevokeSuperAdmin_EnforcesGuardsAndAudits()
    {
        await using var h = new Harness();
        var op1 = await h.CreateUserAsync("op-1", "op1@platform.test", Roles.SuperAdmin);
        var existingUser = await h.CreateUserAsync("user-1", "dev@acme.test", Roles.Developer);
        var actor = new Actor(op1.Id, op1.Email!, "127.0.0.1", "Test", WellKnownTenants.Platform);

        // Cannot revoke self
        var selfRevoke = await h.PlatformAdmin.RevokeSuperAdminAsync(actor, op1.Id);
        Assert.False(selfRevoke.Ok);
        Assert.Contains("own SuperAdmin role", selfRevoke.Error);

        // Promote existing user preserves Developer role
        var promoteRes = await h.PlatformAdmin.GrantSuperAdminAsync(actor, existingUser.Email!);
        Assert.True(promoteRes.Ok, promoteRes.Error);
        Assert.Null(promoteRes.Value);

        // Create brand new operator generates temporary password
        var createRes = await h.PlatformAdmin.GrantSuperAdminAsync(actor, "op2@platform.test", "Operator Two");
        Assert.True(createRes.Ok, createRes.Error);
        Assert.False(string.IsNullOrWhiteSpace(createRes.Value));

        var operators = await h.PlatformAdmin.ListOperatorsAsync();
        Assert.Equal(3, operators.Count);

        // Revoke promoted user works while others remain
        var revokeRes = await h.PlatformAdmin.RevokeSuperAdminAsync(actor, existingUser.Id);
        Assert.True(revokeRes.Ok, revokeRes.Error);

        using (var scope = h.ScopeFactory.CreateScope())
        {
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var devRoles = await userMgr.GetRolesAsync((await userMgr.FindByIdAsync(existingUser.Id))!);
            Assert.DoesNotContain(Roles.SuperAdmin, devRoles);
            Assert.Contains(Roles.Developer, devRoles);
        }

        // Verify Platform audit rows written to WellKnownTenants.Platform
        await using var db = await h.Factory.CreateDbContextAsync();
        var platformAudits = await db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.TenantId == WellKnownTenants.Platform && a.Category == "Platform")
            .ToListAsync();
        Assert.Contains(platformAudits, a => a.Action == "GrantSuperAdmin");
        Assert.Contains(platformAudits, a => a.Action == "RevokeSuperAdmin");
    }

    [Fact]
    public async Task PlatformAdminService_CannotRevokeLastActiveSuperAdmin()
    {
        await using var h = new Harness();
        var op1 = await h.CreateUserAsync("op-1", "op1@platform.test", Roles.SuperAdmin);
        var otherActor = new Actor("external-op", "other@platform.test", "127.0.0.1", "Test", WellKnownTenants.Platform);

        var res = await h.PlatformAdmin.RevokeSuperAdminAsync(otherActor, op1.Id);
        Assert.False(res.Ok);
        Assert.Contains("last active SuperAdmin", res.Error);
    }

    [Fact]
    public async Task TenantQuotas_EnforceMaxDatabasesAndMaxUsers()
    {
        await using var h = new Harness();
        var op = await h.CreateUserAsync("op-1", "op1@platform.test", Roles.SuperAdmin);
        var actor = new Actor(op.Id, op.Email!, "127.0.0.1", "Test", WellKnownTenants.Platform);

        var wsRes = await h.PlatformAdmin.CreateWorkspaceAsync(actor, "Quota Corp", "quota-corp", null, "Standard", maxDatabases: 1, maxUsers: 1, initialAdminEmail: null);
        Assert.True(wsRes.Ok, wsRes.Error);

        Guid quotaTenantId;
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var t = await db.Tenants.IgnoreQueryFilters().SingleAsync(x => x.Slug == "quota-corp");
            quotaTenantId = t.Id;
        }

        h.TenantCtx.SetTenant(quotaTenantId, "quota-corp", "Quota Corp");
        var tenantActor = new Actor(op.Id, op.Email!, "127.0.0.1", "Test", quotaTenantId);

        // Seed environment for DatabaseAdminService.RegisterAsync
        await h.AccessConfig.SaveEnvironmentAsync(tenantActor, null, "QA");

        // User quota = 1
        var u1 = await h.UserAdmin.CreateAsync(tenantActor, "u1@quota.test", "U1", Roles.Developer, null);
        Assert.True(u1.Ok, u1.Error);
        var u2 = await h.UserAdmin.CreateAsync(tenantActor, "u2@quota.test", "U2", Roles.Developer, null);
        Assert.False(u2.Ok);
        Assert.Contains("user limit (1) reached", u2.Error);

        // Database quota = 1
        var d1 = await h.DatabaseAdmin.RegisterAsync(tenantActor, "DbOne", "QA", null, "Host=localhost;Database=db1", 30, 100, DatabaseEngine.PostgreSql);
        Assert.True(d1.Ok, d1.Error);
        var d2 = await h.DatabaseAdmin.RegisterAsync(tenantActor, "DbTwo", "QA", null, "Host=localhost;Database=db2", 30, 100, DatabaseEngine.PostgreSql);
        Assert.False(d2.Ok);
        Assert.Contains("database limit (1) reached", d2.Error);
    }

    [Fact]
    public async Task PlatformAdminService_WorkspaceLifecycle_DomainAllowlistAndDeleteGuards()
    {
        await using var h = new Harness();
        var op = await h.CreateUserAsync("op-1", "op1@platform.test", Roles.SuperAdmin);
        var actor = new Actor(op.Id, op.Email!, "127.0.0.1", "Test", WellKnownTenants.Platform);

        // Cannot delete default workspace
        Assert.False((await h.PlatformAdmin.DeleteWorkspaceAsync(actor, h.DefaultTenantId, "default")).Ok);

        // Create workspace and enforce domain suffix allowlist
        await h.Settings.SetPlatformAsync("Platform:EnforceDomainAllowlist", "true");
        await h.Settings.SetPlatformAsync("Platform:AllowedDomainSuffixes", ".corp.local");

        Assert.True((await h.PlatformAdmin.CreateWorkspaceAsync(actor, "Beta Org", "beta-org", null, "Pro", null, null, null)).Ok);
        Guid betaId;
        await using (var db = await h.Factory.CreateDbContextAsync())
            betaId = (await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Slug == "beta-org")).Id;

        var badDomain = await h.PlatformAdmin.AddDomainAsync(actor, betaId, "sql.evil.com");
        Assert.False(badDomain.Ok);
        var goodDomain = await h.PlatformAdmin.AddDomainAsync(actor, betaId, "sql.beta.corp.local");
        Assert.True(goodDomain.Ok, goodDomain.Error);

        // Cannot delete while active
        Assert.False((await h.PlatformAdmin.DeleteWorkspaceAsync(actor, betaId, "beta-org")).Ok);

        // Disable, mismatch slug fails, matching slug deletes
        Assert.True((await h.PlatformAdmin.ToggleWorkspaceActiveAsync(actor, betaId)).Ok);
        Assert.False((await h.PlatformAdmin.DeleteWorkspaceAsync(actor, betaId, "wrong-slug")).Ok);
        var deleted = await h.PlatformAdmin.DeleteWorkspaceAsync(actor, betaId, "beta-org");
        Assert.True(deleted.Ok, deleted.Error);
    }

    [Fact]
    public async Task SuperAdmin_HasImplicitAuditCapability_AndBypassesMaintenanceMode()
    {
        await using var h = new Harness();
        var superUser = await h.CreateUserAsync("super-1", "super@acme.test", Roles.SuperAdmin);
        var devUser = await h.CreateUserAsync("dev-1", "dev@acme.test", Roles.Developer);

        // Implicit audit access for SuperAdmin
        Assert.True(await h.AccessConfig.CanAuditAsync(superUser.Id));
        Assert.False(await h.AccessConfig.CanAuditAsync(devUser.Id));

        // Maintenance mode blocks developer query execution while allowing SuperAdmin
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = Guid.NewGuid(),
                TenantId = h.DefaultTenantId,
                Name = "TestDb",
                Environment = "QA",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = h.Crypto.Encrypt("Server=localhost;Database=TestDb"),
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        Guid dbId;
        await using (var db = await h.Factory.CreateDbContextAsync())
            dbId = (await db.ManagedDatabases.FirstAsync()).Id;

        await h.Settings.SetPlatformAsync("Platform:MaintenanceMode", "true");
        await h.Settings.SetPlatformAsync("Platform:MaintenanceMessage", "Planned upgrade in progress.");

        var devActor = new Actor(devUser.Id, devUser.Email!, "127.0.0.1", "Test", h.DefaultTenantId);
        var devExec = await h.Execution.ExecuteAsync(devActor, new ExecutionOptions { DatabaseId = dbId, SqlText = "SELECT 1" });
        Assert.Equal("Denied", devExec.Outcome);
        Assert.Contains("Planned upgrade in progress.", devExec.Error);

        // SuperAdmin is NOT blocked by MaintenanceMode (reaches permission check -> Denied due to no SELECT grant, not maintenance)
        var superActor = new Actor(superUser.Id, superUser.Email!, "127.0.0.1", "Test", h.DefaultTenantId);
        var superExec = await h.Execution.ExecuteAsync(superActor, new ExecutionOptions { DatabaseId = dbId, SqlText = "SELECT 1" });
        Assert.DoesNotContain("Planned upgrade in progress.", superExec.Error ?? "");
    }
}
