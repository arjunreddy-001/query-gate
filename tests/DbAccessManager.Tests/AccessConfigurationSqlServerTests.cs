using System.Security.Claims;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DbAccessManager.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUERY_GATE_TEST_SQL")))
            Skip = "Set QUERY_GATE_TEST_SQL to an isolated SQL Server connection with CREATE DATABASE permission.";
    }
}

public class AccessConfigurationSqlServerTests
{
    private static readonly Actor Actor = new("test", "test", null, null);

    [SqlServerFact]
    public async Task Configurable_access_preserves_existing_grants_and_guards_dependencies()
    {
        await WithDatabase(async services =>
        {
            var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
            var access = services.GetRequiredService<AccessConfigurationService>();
            var databases = services.GetRequiredService<DatabaseAdminService>();
            string roleId;
            string userId;
            Guid envId;
            using (var scope = services.CreateScope())
            {
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new IdentityRole("Reporters");
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                var user = new ApplicationUser { UserName = "reporter", Email = "reporter@example.test" };
                Assert.True((await users.CreateAsync(user)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
                roleId = role.Id;
                userId = user.Id;
            }
            Assert.False(await access.CanAuditAsync(userId));
            Assert.True((await access.SaveEnvironmentAsync(Actor, null, "Staging")).Ok);
            Assert.False((await access.SaveEnvironmentAsync(Actor, null, "staging")).Ok);
            await using (var db = await factory.CreateDbContextAsync())
                envId = await db.DatabaseEnvironments.Where(e => e.Name == "Staging").Select(e => e.Id).SingleAsync();
            Assert.True((await access.SaveDefaultsAsync(Actor, envId, roleId, [Perm.Select, "sysproc:sp_example"])).Ok);
            Assert.True((await databases.RegisterAsync(Actor, "First", "Staging", null, "Server=localhost;Database=sample;Integrated Security=true", 30, 100)).Ok);
            Assert.False((await access.DeleteRoleAsync(Actor, roleId)).Ok);
            Assert.False((await access.DeleteEnvironmentAsync(Actor, envId)).Ok);
            Assert.False((await access.SaveRoleAsync(Actor, roleId, "Admin", false)).Ok);
            string? oldStamp;
            await using (var db = await factory.CreateDbContextAsync())
                oldStamp = await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();
            Assert.True((await access.SaveRoleAsync(Actor, roleId, "Analysts", true)).Ok);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.NotEqual(oldStamp, await db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync());
                Assert.Equal("ANALYSTS", await db.Roles.Where(r => r.Id == roleId).Select(r => r.NormalizedName).SingleAsync());
            }
            Assert.True(await access.CanAuditAsync(userId));
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
            var authorization = services.GetRequiredService<IAuthorizationService>();
            Assert.True((await authorization.AuthorizeAsync(principal, null, "AuditAccess")).Succeeded);
            Assert.True((await access.SaveEnvironmentAsync(Actor, envId, "Preproduction")).Ok);
            Assert.True((await access.SaveDefaultsAsync(Actor, envId, roleId, [Perm.Insert])).Ok);
            Assert.True((await databases.RegisterAsync(Actor, "Second", "Preproduction", null, "Server=localhost;Database=sample;Integrated Security=true", 30, 100)).Ok);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var first = await db.ManagedDatabases.SingleAsync(d => d.Name == "First");
                var second = await db.ManagedDatabases.SingleAsync(d => d.Name == "Second");
                Assert.Equal("Preproduction", first.Environment);
                Assert.Equal(new[] { "SELECT", "SYSPROC:SP_EXAMPLE" }, await db.PermissionGrants.Where(g => g.DatabaseId == first.Id && g.RoleId == roleId).OrderBy(g => g.PermissionCode).Select(g => g.PermissionCode).ToArrayAsync());
                Assert.Equal(new[] { "INSERT" }, await db.PermissionGrants.Where(g => g.DatabaseId == second.Id && g.RoleId == roleId).Select(g => g.PermissionCode).ToArrayAsync());
                Assert.True((await databases.UpdateAsync(Actor, first.Id, first.Name, "QA", null, null, 30, 100, true)).Ok);
            }
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(2, await db.PermissionGrants.CountAsync(g => g.DatabaseId == db.ManagedDatabases.Single(d => d.Name == "First").Id && g.RoleId == roleId));
            Assert.True((await access.SaveRoleAsync(Actor, roleId, "Analysts", false)).Ok);
            Assert.False(await access.CanAuditAsync(userId));
            Assert.False((await authorization.AuthorizeAsync(principal, null, "AuditAccess")).Succeeded);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var adminId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).SingleAsync();
                Assert.False((await access.DeleteRoleAsync(Actor, adminId)).Ok);
                Assert.False((await access.SaveRoleAsync(Actor, adminId, "Other", false)).Ok);
            }
            Assert.True((await access.SaveDefaultsAsync(Actor, envId, roleId, [])).Ok);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.PermissionGrants.Where(g => g.RoleId == roleId).ExecuteDeleteAsync();
                await db.UserRoles.Where(ur => ur.RoleId == roleId).ExecuteDeleteAsync();
                await db.ManagedDatabases.Where(d => d.Name == "Second").ExecuteDeleteAsync();
            }
            Assert.True((await access.DeleteRoleAsync(Actor, roleId)).Ok);
            Assert.True((await access.DeleteEnvironmentAsync(Actor, envId)).Ok);
        });
    }

    [SqlServerFact]
    public async Task Upgrade_imports_existing_environments_and_preserves_auditor_and_grants()
    {
        await WithDatabase(async services =>
        {
            var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261108070935_AddEditorPreferences");
                var role = new IdentityRole("Auditor") { NormalizedName = "AUDITOR" };
                var user = new ApplicationUser { UserName = "auditor", NormalizedUserName = "AUDITOR" };
                var database = new ManagedDatabase { Name = "Legacy", Environment = "LegacyEnv", EncryptedConnectionString = "encrypted" };
                db.Roles.Add(role);
                db.Users.Add(user);
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
                db.ManagedDatabases.Add(database);
                db.PermissionGrants.Add(new PermissionGrant { DatabaseId = database.Id, RoleId = role.Id, PermissionCode = Perm.Select });
                await db.SaveChangesAsync();
                await db.Database.MigrateAsync();
                Assert.True(await db.DatabaseEnvironments.AnyAsync(e => e.Name == "LegacyEnv"));
                Assert.True(await db.PermissionGrants.AnyAsync(g => g.DatabaseId == database.Id && g.RoleId == role.Id && g.PermissionCode == Perm.Select));
                Assert.True(await services.GetRequiredService<AccessConfigurationService>().CanAuditAsync(user.Id));
                Assert.Equal(9, await db.EnvironmentDefaultGrants.CountAsync());
            }
        });
    }

    private static async Task WithDatabase(Func<ServiceProvider, Task> test)
    {
        var connection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("QUERY_GATE_TEST_SQL")!)
        {
            InitialCatalog = "QueryGateTest_" + Guid.NewGuid().ToString("N")
        };
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlServer(connection.ConnectionString));
        collection.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        collection.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        collection.AddSingleton<AuditService>();
        collection.AddSingleton<AccessConfigurationService>();
        collection.AddSingleton<IAuthorizationHandler, AuditAuthorizationHandler>();
        collection.AddAuthorization(o => o.AddPolicy("AuditAccess", p => p.RequireAuthenticatedUser().AddRequirements(new AuditRequirement())));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = Convert.ToBase64String(new byte[32]) }).Build();
        collection.AddSingleton(new CryptoService(config, new TestEnvironment()));
        collection.AddSingleton<DatabaseProviderFactory>();
        collection.AddSingleton<DatabaseAdminService>();
        await using var services = collection.BuildServiceProvider();
        try { await test(services); }
        finally
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "QueryGateTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
