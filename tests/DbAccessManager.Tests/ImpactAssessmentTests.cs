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

public class ImpactAssessmentTests
{
    private static readonly Actor OperatorActor = new("operator-1", "operator@example.com", "127.0.0.1", "UnitTestAgent");

    private sealed class TestSqliteProvider : IDatabaseProvider
    {
        private readonly string _connectionString;

        public TestSqliteProvider(string connectionString)
        {
            _connectionString = connectionString;
        }

        public DatabaseEngine Engine => DatabaseEngine.SqlServer;
        public string DisplayName => "Test Sqlite";
        public DbConnection CreateConnection(string connectionString) => new SqliteConnection(_connectionString);
        public (bool Valid, string? Error) ValidateConnectionString(string connectionString) => (true, null);
        public Task<(bool Ok, string Message)> TestAsync(string connectionString) => Task.FromResult((true, "OK"));
        public Task<TargetTelemetry> CollectTelemetryAsync(string connectionString, CancellationToken ct = default) =>
            Task.FromResult(new TargetTelemetry());
        public Task<List<DbObjectInfo>> LoadMetadataAsync(string connectionString, CancellationToken ct = default) =>
            Task.FromResult(new List<DbObjectInfo>());
        public Task<ExecutionPlanDto?> ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct = default) =>
            Task.FromResult<ExecutionPlanDto?>(null);
        public (string Outcome, string ErrorMessage, int? ErrorLine) TranslateException(Exception ex, int timeoutSeconds) =>
            ("Error", ex.Message, null);
        public void RegisterMessageListener(DbConnection connection, Action<string> onMessage) { }
    }

    private sealed class ImpactTestContext : IAsyncDisposable
    {
        public SqliteConnection AppConnection { get; }
        public SqliteConnection TargetDbConnection { get; }
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

        public ImpactTestContext()
        {
            var targetDbName = $"targetdb_{Guid.NewGuid():N}";
            var targetConnStr = $"Data Source=file:{targetDbName}?mode=memory&cache=shared";

            AppConnection = new SqliteConnection("Data Source=:memory:");
            AppConnection.Open();

            TargetDbConnection = new SqliteConnection(targetConnStr);
            TargetDbConnection.Open();

            // Seed target test database tables
            using (var cmd = TargetDbConnection.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY, name TEXT, email TEXT, active INTEGER);
                    INSERT INTO users (id, name, email, active) VALUES (1, 'Alice', 'alice@test.com', 1);
                    INSERT INTO users (id, name, email, active) VALUES (2, 'Bob', 'bob@test.com', 1);
                    INSERT INTO users (id, name, email, active) VALUES (3, 'Charlie', 'charlie@test.com', 0);

                    CREATE TABLE IF NOT EXISTS audit_records (id INTEGER PRIMARY KEY, details TEXT);
                    INSERT INTO audit_records (id, details) VALUES (1, 'Event 1');
                    INSERT INTO audit_records (id, details) VALUES (2, 'Event 2');
                ";
                cmd.ExecuteNonQuery();
            }

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(AppConnection)
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

            var customProviders = new Dictionary<DatabaseEngine, IDatabaseProvider>
            {
                [DatabaseEngine.SqlServer] = new TestSqliteProvider(targetConnStr)
            };
            Providers = new DatabaseProviderFactory(customProviders);
            UserCache = new UserStatusCache(memCache);
            DbCache = new DatabaseCache(Factory, Crypto, memCache);

            var services = new ServiceCollection();
            services.AddLogging();
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
            await TargetDbConnection.DisposeAsync();
            await AppConnection.DisposeAsync();
        }
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    [Fact]
    public void SqlGate_HasMutatingStatements_DetectsMutatingKeywords()
    {
        var gate = new SqlGate();

        // Mutating statements
        Assert.True(gate.Analyze("UPDATE users SET active = 0 WHERE id = 1;").HasMutatingStatements);
        Assert.True(gate.Analyze("DELETE FROM users WHERE id = 1;").HasMutatingStatements);
        Assert.True(gate.Analyze("INSERT INTO users (id, name) VALUES (4, 'David');").HasMutatingStatements);
        Assert.True(gate.Analyze("DROP TABLE users;").HasMutatingStatements);
        Assert.True(gate.Analyze("TRUNCATE TABLE users;").HasMutatingStatements);

        // Read-only statements
        Assert.False(gate.Analyze("SELECT * FROM users WHERE active = 1;").HasMutatingStatements);
        Assert.False(gate.Analyze("SELECT COUNT(*) FROM users;").HasMutatingStatements);
    }

    [Fact]
    public async Task AssessImpactAsync_SelectOnly_ReportsNoImpact()
    {
        await using var ctx = new ImpactTestContext();
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
                Name = "TestDb",
                Engine = DatabaseEngine.SqlServer,
                Environment = "Development",
                IsActive = true,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Data Source=file:targetdb?mode=memory&cache=shared")
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.AssessImpactAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "SELECT * FROM users WHERE active = 1;"
        });

        Assert.False(result.HasImpact);
        Assert.Empty(result.Items);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AssessImpactAsync_Update_ReportsExactRowsAffectedAndPreservesData()
    {
        await using var ctx = new ImpactTestContext();
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
                Name = "TestDb",
                Engine = DatabaseEngine.SqlServer,
                Environment = "Development",
                IsActive = true,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Data Source=file:targetdb?mode=memory&cache=shared")
            });
            await db.SaveChangesAsync();
        }

        // Updating 2 records with active = 1
        var result = await ctx.Execution.AssessImpactAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "UPDATE users SET active = 0 WHERE active = 1;"
        });

        Assert.True(result.HasImpact);
        Assert.Equal(2, result.TotalRowsAffected);
        Assert.NotEmpty(result.Items);
        var item = result.Items[0];
        Assert.Equal("UPDATE", item.StatementType);
        Assert.Equal(2, item.RowsAffected);
        Assert.Contains("2 record(s)", item.Summary);

        // Verify data was NOT actually changed (rolled back!)
        using var cmd = ctx.TargetDbConnection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE active = 1;";
        var activeCount = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(2, activeCount);
    }

    [Fact]
    public async Task AssessImpactAsync_Delete_ReportsAffectedRowsAndRollsBack()
    {
        await using var ctx = new ImpactTestContext();
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
                Name = "TestDb",
                Engine = DatabaseEngine.SqlServer,
                Environment = "Development",
                IsActive = true,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Data Source=file:targetdb?mode=memory&cache=shared")
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.AssessImpactAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "DELETE FROM users WHERE id = 1;"
        });

        Assert.True(result.HasImpact);
        Assert.Equal(1, result.TotalRowsAffected);
        Assert.NotEmpty(result.Items);
        var item = result.Items[0];
        Assert.Equal("DELETE", item.StatementType);
        Assert.Equal("danger", item.Severity);

        // Verify data was NOT deleted
        using var cmd = ctx.TargetDbConnection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE id = 1;";
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AssessImpactAsync_DropTable_SafelyFetchesExistingRowCountWithoutDropping()
    {
        await using var ctx = new ImpactTestContext();
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
                Name = "TestDb",
                Engine = DatabaseEngine.SqlServer,
                Environment = "Production",
                IsActive = true,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Data Source=file:targetdb?mode=memory&cache=shared")
            });
            await db.SaveChangesAsync();
        }

        var result = await ctx.Execution.AssessImpactAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "DROP TABLE users;"
        });

        Assert.True(result.HasImpact);
        Assert.True(result.IsProduction);
        Assert.NotEmpty(result.Items);
        var item = result.Items.FirstOrDefault(i => i.StatementType == "DROP");
        Assert.NotNull(item);
        Assert.Equal(3, item.ExistingRowCount); // 3 records in users table
        Assert.Equal("danger", item.Severity);
        Assert.Contains("3 record(s)", item.Summary);

        // Verify table was NOT dropped!
        using var cmd = ctx.TargetDbConnection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users;";
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task AssessImpactAsync_UnfilteredWrite_FlagsWarning()
    {
        await using var ctx = new ImpactTestContext();
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
                Name = "TestDb",
                Engine = DatabaseEngine.SqlServer,
                Environment = "Development",
                IsActive = true,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Data Source=file:targetdb?mode=memory&cache=shared")
            });
            await db.SaveChangesAsync();
        }

        // UPDATE without WHERE
        var result = await ctx.Execution.AssessImpactAsync(OperatorActor, new ExecutionOptions
        {
            DatabaseId = dbId,
            SqlText = "UPDATE users SET active = 0;"
        });

        Assert.True(result.HasImpact);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Unfiltered write"));
    }
}
