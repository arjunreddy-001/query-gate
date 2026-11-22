using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class DatabaseAdminServiceTests
{
    private static readonly Actor AdminActor = new("admin-user-id", "admin@example.com", "127.0.0.1", "TestClient");

    private sealed class AdminTestContext : IAsyncDisposable
    {
        public SqliteConnection Connection { get; }
        public IDbContextFactory<ApplicationDbContext> Factory { get; }
        public CryptoService Crypto { get; }
        public AuditService Audit { get; }
        public DatabaseProviderFactory Providers { get; }
        public DatabaseAdminService Service { get; }

        public AdminTestContext()
        {
            Connection = new SqliteConnection("Data Source=:memory:");
            Connection.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Connection)
                .Options;

            using (var initialDb = new ApplicationDbContext(options))
            {
                initialDb.Database.EnsureCreated();
            }

            Factory = new SimpleDbContextFactory(options);
            var inMemorySettings = new Dictionary<string, string?>
            {
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            Crypto = new CryptoService(config);
            Audit = new AuditService(Factory, NullLogger<AuditService>.Instance);
            Providers = new DatabaseProviderFactory();
            Service = new DatabaseAdminService(Factory, Crypto, Audit, Providers);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    [Fact]
    public async Task RegisterAsync_FailsOnInvalidConnectionString()
    {
        await using var ctx = new AdminTestContext();
        var result = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "OrdersDb",
            environment: "Production",
            description: null,
            connectionString: "InvalidConnectionStringWithoutAnySemicolonOrEqual",
            timeout: 30,
            maxRows: 1000,
            engine: DatabaseEngine.SqlServer);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RegisterAsync_FailsOnMissingEnvironment()
    {
        await using var ctx = new AdminTestContext();
        const string validConn = "Server=localhost,1433;Database=TestDb;User Id=sa;Password=secret;TrustServerCertificate=True;";

        var result = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "OrdersDb",
            environment: "NonExistentEnv",
            description: null,
            connectionString: validConn,
            timeout: 30,
            maxRows: 1000,
            engine: DatabaseEngine.SqlServer);

        Assert.False(result.Ok);
        Assert.Equal("Environment not found.", result.Error);
    }

    [Fact]
    public async Task RegisterAsync_FailsOnDuplicateNameInSameEnvironment()
    {
        await using var ctx = new AdminTestContext();
        const string validConn = "Server=localhost,1433;Database=TestDb;User Id=sa;Password=secret;TrustServerCertificate=True;";

        // Seed environment
        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.DatabaseEnvironments.Add(new DatabaseEnvironment { Name = "Production" });
            await db.SaveChangesAsync();
        }

        var res1 = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "OrdersDb",
            environment: "Production",
            description: null,
            connectionString: validConn,
            timeout: 30,
            maxRows: 1000,
            engine: DatabaseEngine.SqlServer);
        Assert.True(res1.Ok);

        var res2 = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "OrdersDb",
            environment: "Production",
            description: null,
            connectionString: validConn,
            timeout: 30,
            maxRows: 1000,
            engine: DatabaseEngine.SqlServer);
        Assert.False(res2.Ok);
        Assert.Equal("A database with that name and environment is already registered.", res2.Error);
    }

    [Fact]
    public async Task RegisterAsync_FailsOnScopeConflict()
    {
        await using var ctx = new AdminTestContext();
        const string validConn = "Server=localhost,1433;Database=TestDb;User Id=sa;Password=secret;TrustServerCertificate=True;";

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.DatabaseEnvironments.Add(new DatabaseEnvironment { Name = "Staging" });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "ConflictDb",
            environment: "Staging",
            description: null,
            connectionString: validConn,
            timeout: 30,
            maxRows: 1000,
            engine: DatabaseEngine.SqlServer,
            includedObjects: "dbo.Customers",
            excludedObjects: "dbo.Customers");

        Assert.False(result.Ok);
        Assert.Contains("Conflict detected", result.Error);
    }

    [Fact]
    public async Task RegisterAsync_SucceedsAndAppliesEnvironmentDefaultGrants()
    {
        await using var ctx = new AdminTestContext();
        const string validConn = "Host=localhost;Port=5432;Database=pgtest;Username=postgres;Password=secret";
        var envId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            var env = new DatabaseEnvironment { Id = envId, Name = "Development" };
            db.DatabaseEnvironments.Add(env);

            var role = new IdentityRole { Id = "role-devs", Name = "Developers", NormalizedName = "DEVELOPERS" };
            db.Roles.Add(role);

            db.EnvironmentDefaultGrants.Add(new EnvironmentDefaultGrant
            {
                EnvironmentId = envId,
                RoleId = "role-devs",
                PermissionCode = Perm.Select
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "AnalyticsPg",
            environment: "Development",
            description: "Analytics test db",
            connectionString: validConn,
            timeout: 60,
            maxRows: 5000,
            engine: DatabaseEngine.PostgreSql,
            includedObjects: "public.events",
            excludedObjects: "public.secrets");

        Assert.True(result.Ok);

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            var registered = await db.ManagedDatabases.FirstOrDefaultAsync(d => d.Name == "AnalyticsPg");
            Assert.NotNull(registered);
            Assert.Equal("Development", registered.Environment);
            Assert.Equal(DatabaseEngine.PostgreSql, registered.Engine);
            Assert.Equal(60, registered.DefaultTimeoutSeconds);
            Assert.Equal(5000, registered.MaxRows);
            Assert.True(registered.IsActive);
            Assert.Equal("public.events", registered.IncludedObjects);
            Assert.Equal("public.secrets", registered.ExcludedObjects);

            // Decrypted connection string matches
            var decrypted = ctx.Crypto.Decrypt(registered.EncryptedConnectionString);
            Assert.Equal(validConn, decrypted);

            // Default grant was applied
            var grant = await db.PermissionGrants.FirstOrDefaultAsync(g => g.DatabaseId == registered.Id);
            Assert.NotNull(grant);
            Assert.Equal("role-devs", grant.RoleId);
            Assert.Equal(Perm.Select, grant.PermissionCode);
        }
    }

    [Fact]
    public async Task UpdateAsync_UpdatesPropertiesAndReEncryptsNewConnectionString()
    {
        await using var ctx = new AdminTestContext();
        const string initialConn = "Host=localhost;Port=5432;Database=appdb;Username=postgres;Password=initial";
        const string updatedConn = "Host=localhost;Port=5432;Database=appdb;Username=postgres;Password=newsecret";
        var envId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.DatabaseEnvironments.Add(new DatabaseEnvironment { Id = envId, Name = "Production" });
            db.DatabaseEnvironments.Add(new DatabaseEnvironment { Name = "Staging" });
            await db.SaveChangesAsync();
        }

        var regResult = await ctx.Service.RegisterAsync(
            AdminActor,
            name: "AppDb",
            environment: "Production",
            description: "Initial description",
            connectionString: initialConn,
            timeout: 30,
            maxRows: 1000,
            engine: DatabaseEngine.PostgreSql);
        Assert.True(regResult.Ok);

        Guid dbId;
        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            var entity = await db.ManagedDatabases.FirstAsync(d => d.Name == "AppDb");
            dbId = entity.Id;
        }

        // Update properties and connection string
        var updateResult = await ctx.Service.UpdateAsync(
            AdminActor,
            id: dbId,
            name: "AppDb-Renamed",
            environment: "Staging",
            description: "Updated description",
            newConnectionString: updatedConn,
            timeout: 45,
            maxRows: 2500,
            active: true,
            engine: DatabaseEngine.PostgreSql,
            includedObjects: "public.users",
            excludedObjects: null);

        Assert.True(updateResult.Ok);

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            var updated = await db.ManagedDatabases.FindAsync(dbId);
            Assert.NotNull(updated);
            Assert.Equal("AppDb-Renamed", updated.Name);
            Assert.Equal("Staging", updated.Environment);
            Assert.Equal("Updated description", updated.Description);
            Assert.Equal(45, updated.DefaultTimeoutSeconds);
            Assert.Equal(2500, updated.MaxRows);
            Assert.Equal("public.users", updated.IncludedObjects);
            Assert.Null(updated.ExcludedObjects);

            var decrypted = ctx.Crypto.Decrypt(updated.EncryptedConnectionString);
            Assert.Equal(updatedConn, decrypted);
        }
    }

    [Fact]
    public async Task RoleScopeRules_CreateUpdateAndDelete_WorkCorrectly()
    {
        await using var ctx = new AdminTestContext();
        var dbId = Guid.NewGuid();
        const string roleId = "role-analyst";

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.DatabaseEnvironments.Add(new DatabaseEnvironment { Name = "Prod" });
            db.Roles.Add(new IdentityRole { Id = roleId, Name = "Analyst", NormalizedName = "ANALYST" });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "FinanceDb",
                Environment = "Prod",
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=FinanceDb;")
            });
            await db.SaveChangesAsync();
        }

        // Save rule
        var saveResult = await ctx.Service.SaveRoleScopeRuleAsync(AdminActor, dbId, roleId, "dbo.Ledger", "dbo.Audit");
        Assert.True(saveResult.Ok);

        var rules = await ctx.Service.GetRoleScopeRulesAsync(dbId);
        Assert.Single(rules);
        Assert.Equal(roleId, rules[0].RoleId);
        Assert.Equal("dbo.Ledger", rules[0].IncludedObjects);
        Assert.Equal("dbo.Audit", rules[0].ExcludedObjects);

        // Update rule
        var updateResult = await ctx.Service.SaveRoleScopeRuleAsync(AdminActor, dbId, roleId, "dbo.Ledger, dbo.Transactions", null);
        Assert.True(updateResult.Ok);

        var updatedRules = await ctx.Service.GetRoleScopeRulesAsync(dbId);
        Assert.Single(updatedRules);
        Assert.Equal("dbo.Ledger, dbo.Transactions", updatedRules[0].IncludedObjects);
        Assert.Null(updatedRules[0].ExcludedObjects);

        // Delete rule
        var delResult = await ctx.Service.DeleteRoleScopeRuleAsync(AdminActor, dbId, roleId);
        Assert.True(delResult.Ok);

        var remainingRules = await ctx.Service.GetRoleScopeRulesAsync(dbId);
        Assert.Empty(remainingRules);
    }

    [Fact]
    public async Task UpdateDatabaseObjectRulesAsync_ValidatesConflictAndUpdates()
    {
        await using var ctx = new AdminTestContext();
        var dbId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "InventoryDb",
                Environment = "Prod",
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=InventoryDb;")
            });
            await db.SaveChangesAsync();
        }

        // Conflict check
        var conflictResult = await ctx.Service.UpdateDatabaseObjectRulesAsync(AdminActor, dbId, "dbo.Items", "dbo.Items");
        Assert.False(conflictResult.Ok);
        Assert.Contains("Conflict detected", conflictResult.Error);

        // Valid update
        var validResult = await ctx.Service.UpdateDatabaseObjectRulesAsync(AdminActor, dbId, "dbo.Items, dbo.Stock", "dbo.Hidden");
        Assert.True(validResult.Ok);

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            var dbEntity = await db.ManagedDatabases.FindAsync(dbId);
            Assert.NotNull(dbEntity);
            Assert.Equal("dbo.Items, dbo.Stock", dbEntity.IncludedObjects);
            Assert.Equal("dbo.Hidden", dbEntity.ExcludedObjects);
        }
    }
}
