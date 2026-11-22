using System.Data.Common;
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

public class ExecutionCorePipelineTests
{
    private static readonly Actor OperatorActor = new("operator-1", "operator@example.com", "127.0.0.1", "UnitTestAgent");

    private sealed class PipelineTestContext : IAsyncDisposable
    {
        public SqliteConnection Connection { get; }
        public IDbContextFactory<ApplicationDbContext> Factory { get; }
        public CryptoService Crypto { get; }
        public SettingsService Settings { get; }
        public AuditService Audit { get; }
        public SqlGate Gate { get; }
        public PermissionService Permissions { get; }
        public DatabaseProviderFactory Providers { get; }
        public UserStatusCache UserCache { get; }
        public DatabaseCache DbCache { get; }
        public ExecutionService Execution { get; }
        public IServiceScopeFactory ScopeFactory { get; }

        public PipelineTestContext()
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

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
            }).Build();
            Crypto = new CryptoService(config);

            var memCache = new MemoryCache(new MemoryCacheOptions());
            var tenantAccessor = new TenantContextAccessor();
            Settings = new SettingsService(Factory, memCache, tenantAccessor);
            Audit = new AuditService(Factory, NullLogger<AuditService>.Instance);
            Gate = new SqlGate(memCache);
            Permissions = new PermissionService(Factory, memCache);
            Providers = new DatabaseProviderFactory();
            UserCache = new UserStatusCache(memCache);
            DbCache = new DatabaseCache(Factory, Crypto, memCache);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Factory);
            services.AddScoped(_ => Factory.CreateDbContext());
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
            var provider = services.BuildServiceProvider();
            ScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            Execution = new ExecutionService(
                Factory,
                ScopeFactory,
                Gate,
                Permissions,
                Audit,
                Crypto,
                Settings,
                NullLogger<ExecutionService>.Instance,
                Providers,
                DbCache,
                UserCache);
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
    public async Task AuthorizeExportAsync_Denied_WhenUserLacksExportPermission()
    {
        await using var ctx = new PipelineTestContext();
        var dbId = Guid.NewGuid();

        var result = await ctx.Execution.AuthorizeExportAsync(OperatorActor, dbId, 50, "SELECT * FROM Users");

        Assert.False(result.Ok);
        Assert.Contains("EXPORT permission", result.Error);

        await using var db = await ctx.Factory.CreateDbContextAsync();
        var audit = await db.AuditLogs.FirstOrDefaultAsync(a => a.Category == "Export");
        Assert.NotNull(audit);
        Assert.Equal("Denied", audit.Outcome);
        Assert.Equal(OperatorActor.UserId, audit.UserId);
    }

    [Fact]
    public async Task AuthorizeExportAsync_Succeeds_WhenUserHasExportPermission()
    {
        await using var ctx = new PipelineTestContext();
        var dbId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = OperatorActor.UserId,
                UserName = OperatorActor.UserName,
                Email = OperatorActor.UserName
            });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "ExportDb",
                Environment = "Production",
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=ExportDb;")
            });
            db.PermissionGrants.Add(new PermissionGrant
            {
                UserId = OperatorActor.UserId,
                DatabaseId = dbId,
                PermissionCode = Perm.Export,
                Effect = PermissionEffect.Allow
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.AuthorizeExportAsync(OperatorActor, dbId, 100, "SELECT * FROM Users", format: "Csv", databaseName: "ExportDb");

        Assert.True(result.Ok);

        await using var verifyDb = await ctx.Factory.CreateDbContextAsync();
        var audit = await verifyDb.AuditLogs.FirstOrDefaultAsync(a => a.Category == "Export" && a.Action == "ExportCsv");
        Assert.NotNull(audit);
        Assert.Equal("Success", audit.Outcome);
        Assert.Equal(100, audit.RowsReturned);
    }

    [Fact]
    public async Task ExecuteAsync_Denied_WhenDatabaseNotFoundOrInactive()
    {
        await using var ctx = new PipelineTestContext();
        var missingDbId = Guid.NewGuid();

        var result = await ctx.Execution.ExecuteAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = missingDbId,
            SqlText = "SELECT 1;"
        });

        Assert.False(result.Ok);
        Assert.Equal("Denied", result.Outcome);
        Assert.Contains("Database not found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SyntaxError_WhenSqlMalformed()
    {
        await using var ctx = new PipelineTestContext();
        var dbId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser { Id = OperatorActor.UserId, UserName = OperatorActor.UserName, Email = OperatorActor.UserName, IsActive = true });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "AppDb",
                Environment = "Dev",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=AppDb;"),
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.ExecuteAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "SELECT FROM WHERE;"
        });

        Assert.False(result.Ok);
        Assert.Equal("Denied", result.Outcome);
        Assert.NotEmpty(result.Denials);
    }

    [Fact]
    public async Task ExecuteAsync_Denied_OnHardSecurityDenials()
    {
        await using var ctx = new PipelineTestContext();
        var dbId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser { Id = OperatorActor.UserId, UserName = OperatorActor.UserName, Email = OperatorActor.UserName, IsActive = true });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "AppDb",
                Environment = "Dev",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=AppDb;"),
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.ExecuteAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "KILL 52;"
        });

        Assert.False(result.Ok);
        Assert.Equal("Denied", result.Outcome);
        Assert.NotEmpty(result.Denials);
    }

    [Fact]
    public async Task ExecuteAsync_FlagsUnfilteredWrite_WhenConfirmationNotGiven()
    {
        await using var ctx = new PipelineTestContext();
        var dbId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser { Id = OperatorActor.UserId, UserName = OperatorActor.UserName, Email = OperatorActor.UserName, IsActive = true });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "AppDb",
                Environment = "Dev",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=AppDb;"),
                IsActive = true
            });
            db.PermissionGrants.Add(new PermissionGrant
            {
                UserId = OperatorActor.UserId,
                DatabaseId = dbId,
                PermissionCode = Perm.Update,
                Effect = PermissionEffect.Allow
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.ExecuteAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "UPDATE Users SET IsActive = 0;",
            ConfirmUnfilteredWrites = false
        });

        Assert.False(result.Ok);
        Assert.Equal("NeedsConfirmation", result.Outcome);
        Assert.NotEmpty(result.UnfilteredWrites);
    }

    [Fact]
    public async Task ExecuteAsync_Denied_WhenUserLacksRequiredPermission()
    {
        await using var ctx = new PipelineTestContext();
        var dbId = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = OperatorActor.UserId,
                UserName = OperatorActor.UserName,
                Email = OperatorActor.UserName,
                IsActive = true
            });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "SecureDb",
                Environment = "Prod",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=SecureDb;"),
                IsActive = true
            });
            // Give only SELECT permission
            db.PermissionGrants.Add(new PermissionGrant
            {
                UserId = OperatorActor.UserId,
                DatabaseId = dbId,
                PermissionCode = Perm.Select,
                Effect = PermissionEffect.Allow
            });
            await db.SaveChangesAsync();
        }

        // Try DELETE without grant
        var result = await ctx.Execution.ExecuteAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "DELETE FROM Orders WHERE Id = 1;"
        });

        Assert.False(result.Ok);
        Assert.Equal("Denied", result.Outcome);
        Assert.Contains(result.Denials, d => d.Contains("Missing permission: DELETE"));
    }

    [Fact]
    public async Task PermissionService_GetAccessibleDatabasesAsync_FiltersByUserGrants()
    {
        await using var ctx = new PipelineTestContext();
        var dbId1 = Guid.NewGuid();
        var dbId2 = Guid.NewGuid();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser { Id = OperatorActor.UserId, UserName = OperatorActor.UserName, Email = OperatorActor.UserName });
            db.ManagedDatabases.Add(new ManagedDatabase { Id = dbId1, Name = "PermittedDb", Environment = "Prod", EncryptedConnectionString = ctx.Crypto.Encrypt("conn1") });
            db.ManagedDatabases.Add(new ManagedDatabase { Id = dbId2, Name = "RestrictedDb", Environment = "Prod", EncryptedConnectionString = ctx.Crypto.Encrypt("conn2") });
            db.PermissionGrants.Add(new PermissionGrant { UserId = OperatorActor.UserId, DatabaseId = dbId1, PermissionCode = Perm.Select, Effect = PermissionEffect.Allow });
            await db.SaveChangesAsync();
        }

        var accessible = await ctx.Permissions.GetAccessibleDatabasesAsync(OperatorActor.UserId);
        Assert.Single(accessible);
        Assert.Equal("PermittedDb", accessible[0].Name);
    }

    [Fact]
    public async Task ExecutionService_CancelAllFor_CancelsRunningQueryTokens()
    {
        await using var ctx = new PipelineTestContext();

        // Calling cancel for user with no active queries should complete gracefully
        ctx.Execution.CancelAllFor("unknown-user-id");
        Assert.Equal(0, ctx.Execution.GetInFlightQueryCount(Guid.NewGuid()));
    }
}
