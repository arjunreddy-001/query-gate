using System.Data.Common;
using Bunit;
using DbAccessManager.Components.Pages.QueryWorkspace;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace DbAccessManager.Tests;

public class PerformanceOptimizationTests : BunitContext
{
    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    private sealed class CountingMetadataProvider : IDatabaseProvider
    {
        private int _loadCalls;
        public int LoadCalls => Volatile.Read(ref _loadCalls);

        public DatabaseEngine Engine => DatabaseEngine.SqlServer;
        public string DisplayName => "Fake";
        public DbConnection CreateConnection(string connectionString) => throw new NotSupportedException();
        public (bool Valid, string? Error) ValidateConnectionString(string connectionString) => (true, null);
        public Task<(bool Ok, string Message)> TestAsync(string connectionString) => Task.FromResult((true, "OK"));
        public Task<TargetTelemetry> CollectTelemetryAsync(string connectionString, CancellationToken ct = default) =>
            Task.FromResult(new TargetTelemetry(null, null, null, null, null));

        public async Task<List<DbObjectInfo>> LoadMetadataAsync(string connectionString, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loadCalls);
            await Task.Delay(80, ct);
            return [new DbObjectInfo("dbo", "Orders", "Table", ["Id", "CustomerId"])];
        }

        public Task<ExecutionPlanDto?> ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct = default) =>
            Task.FromResult<ExecutionPlanDto?>(null);
        public (string Outcome, string ErrorMessage, int? ErrorLine) TranslateException(Exception ex, int timeoutSeconds) =>
            ("Error", ex.Message, null);
        public void RegisterMessageListener(DbConnection connection, Action<string> onMessage) { }
    }

    [Fact]
    public async Task MetadataService_GetAsync_CoalescesConcurrentRequests()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        await using (var initDb = new ApplicationDbContext(options))
        {
            await initDb.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
        }).Build();
        var crypto = new CryptoService(config);
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var settings = new SettingsService(factory, memCache, new TenantContextAccessor());

        var dbId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "SalesDb",
                Environment = "Production",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = crypto.Encrypt("Server=.;Database=SalesDb;"),
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var fakeProvider = new CountingMetadataProvider();
        var providers = new DatabaseProviderFactory(new Dictionary<DatabaseEngine, IDatabaseProvider>
        {
            [DatabaseEngine.SqlServer] = fakeProvider
        });
        var metadata = new MetadataService(factory, crypto, memCache, settings, providers);

        var tasks = Enumerable.Range(0, 10).Select(_ => metadata.GetAsync(dbId)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, fakeProvider.LoadCalls);
        Assert.All(results, r => Assert.Single(r));
    }

    [Fact]
    public async Task PermissionService_GetAccessibleDatabasesAsync_BatchesAndPopulatesCache()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        await using (var initDb = new ApplicationDbContext(options))
        {
            await initDb.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory(options);
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var perms = new PermissionService(factory, memCache);

        var db1 = new ManagedDatabase { Id = Guid.NewGuid(), Name = "Alpha", Environment = "Dev", EncryptedConnectionString = "v1:x", IsActive = true };
        var db2 = new ManagedDatabase { Id = Guid.NewGuid(), Name = "Beta", Environment = "Dev", EncryptedConnectionString = "v1:y", IsActive = true };
        var db3 = new ManagedDatabase { Id = Guid.NewGuid(), Name = "Gamma", Environment = "Dev", EncryptedConnectionString = "v1:z", IsActive = false };

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser { Id = "u1", UserName = "u1@example.com", Email = "u1@example.com" });
            db.Roles.Add(new IdentityRole { Id = "r1", Name = "Analyst", NormalizedName = "ANALYST" });
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = "u1", RoleId = "r1" });
            db.ManagedDatabases.AddRange(db1, db2, db3);
            db.PermissionGrants.Add(new PermissionGrant { DatabaseId = db1.Id, RoleId = "r1", PermissionCode = Perm.Select, Effect = PermissionEffect.Allow });
            db.PermissionGrants.Add(new PermissionGrant { DatabaseId = db2.Id, UserId = "u1", PermissionCode = Perm.Insert, Effect = PermissionEffect.Allow });
            await db.SaveChangesAsync();
        }

        var accessible = await perms.GetAccessibleDatabasesAsync("u1");
        Assert.Equal(2, accessible.Count);
        Assert.Contains(accessible, d => d.Id == db1.Id);
        Assert.Contains(accessible, d => d.Id == db2.Id);

        // Cache should now be warm for db1 and db2
        Assert.True(memCache.TryGetValue($"perm:rules:{Guid.Empty}:u1:{db1.Id}", out List<PermissionRule>? cached1));
        Assert.NotNull(cached1);
        Assert.True(memCache.TryGetValue($"perm:rules:{Guid.Empty}:u1:{db2.Id}", out List<PermissionRule>? cached2));
        Assert.NotNull(cached2);
    }

    [Fact]
    public async Task AppDbLoggerProvider_BatchesQueuedEntriesToDatabase()
    {
        var services = new ServiceCollection();
        var dbName = "LoggerBatchTests_" + Guid.NewGuid();
        services.AddDbContextFactory<ApplicationDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<ITenantContext, TenantContext>();
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        var sp = services.BuildServiceProvider();

        using (var provider = new AppDbLoggerProvider(sp.GetRequiredService<IServiceScopeFactory>()))
        {
            var logger = provider.CreateLogger("DbAccessManager.TestLogger");
            logger.LogError("Error one");
            logger.LogWarning("Warning two");
            logger.LogError("Error three");
            await Task.Delay(450);
        }

        var factory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var logs = await db.AppLogs.IgnoreQueryFilters().OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.Contains(logs, l => l.Message == "Error one" && l.Level == "Error");
        Assert.Contains(logs, l => l.Message == "Warning two" && l.Level == "Warning");
        Assert.Contains(logs, l => l.Message == "Error three" && l.Level == "Error");
    }

    [Fact]
    public void ExportDownloadCache_RegistersAndTakesOnce_RejectsWrongUser()
    {
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new ExportDownloadCache(memCache);
        var payload = new byte[] { 1, 2, 3, 4 };

        var token = cache.Register(payload, "application/octet-stream", "out.xlsx", "user-a");

        Assert.False(cache.TryTake(token, "user-b", out _));
        Assert.True(cache.TryTake(token, "user-a", out var entry));
        Assert.NotNull(entry);
        Assert.Equal("out.xlsx", entry!.FileName);
        Assert.Equal(payload, entry.Bytes);

        // Single-use: second retrieval fails
        Assert.False(cache.TryTake(token, "user-a", out _));
    }

    [Fact]
    public void QueryResultProcessor_FiltersAndSortsDeterministically()
    {
        var rs = new ResultSetDto
        {
            Columns = ["Id", "Name"],
            Rows =
            [
                [10, "Charlie"],
                [2, "Alice"],
                [5, "Bob"]
            ]
        };

        var sorted = QueryResultProcessor.ProcessRows(rs, null, 0, true);
        Assert.Equal(3, sorted.Count);
        Assert.Equal(2, sorted[0].Values[0]);
        Assert.Equal(5, sorted[1].Values[0]);
        Assert.Equal(10, sorted[2].Values[0]);

        var filtered = QueryResultProcessor.ProcessRows(rs, "ali", null, true);
        Assert.Single(filtered);
        Assert.Equal("Alice", filtered[0].Values[1]);
    }

    [Fact]
    public void QueryWorkspacePanels_RenderObjectsAndResultsInBunit()
    {
        var objects = new List<DbObjectInfo>
        {
            new("dbo", "Orders", "Table", ["OrderId", "Total"]),
            new("dbo", "Customers", "Table", ["CustomerId", "Name"])
        };

        var explorer = Render<ObjectExplorerPanel>(p => p
            .Add(x => x.Side, "explorer")
            .Add(x => x.Objects, objects)
            .Add(x => x.Expanded, new HashSet<string>())
            .Add(x => x.OutcomeClass, _ => "ok")
            .Add(x => x.Ago, _ => "just now"));

        Assert.Contains("dbo.Orders", explorer.Markup);
        Assert.Contains("dbo.Customers", explorer.Markup);

        var result = new ExecutionResult { Outcome = "Success" };
        result.ResultSets.Add(new ResultSetDto
        {
            Columns = ["Id", "Label"],
            Rows = [[1, "RowOne"], [2, "RowTwo"]]
        });

        var grid = Render<ResultsGridPanel>(p => p
            .Add(x => x.Last, result)
            .Add(x => x.ActiveResult, 0)
            .Add(x => x.CanExport, true)
            .Add(x => x.OutcomeClass, _ => "ok"));

        Assert.Contains("RowOne", grid.Markup);
        Assert.Contains("RowTwo", grid.Markup);
        Assert.Contains("Export", grid.Markup);
    }
}
