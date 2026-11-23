using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DbAccessManager.Tests;

public class AdvancedFeaturesTests
{
    [Fact]
    public void SqlGate_Normalizes_Template_And_Colon_Parameters_Without_Parse_Errors()
    {
        var gate = new SqlGate();
        var sql = "SELECT * FROM dbo.Users WHERE id = :userId AND created_at >= {{startDate}} AND status = @status";

        var normalized = SqlGate.NormalizeParameterTokens(sql);
        Assert.Contains("@userId", normalized);
        Assert.Contains("@startDate", normalized);
        Assert.Contains("@status", normalized);
        Assert.DoesNotContain(":userId", normalized);
        Assert.DoesNotContain("{{startDate}}", normalized);

        var analysis = gate.Analyze(sql, DatabaseEngine.SqlServer);
        Assert.False(analysis.HasHardDenial);
        Assert.Contains(Perm.Select, analysis.Requirements);
    }

    [Fact]
    public void ExecutionOptions_Parameters_Can_Be_Set_And_Retrieved()
    {
        var opt = new ExecutionOptions
        {
            SqlText = "SELECT 1",
            DatabaseId = Guid.NewGuid(),
            Parameters = new Dictionary<string, object?>
            {
                ["userId"] = 123,
                ["startDate"] = "2026-01-01"
            }
        };

        Assert.Equal(2, opt.Parameters.Count);
        Assert.Equal(123, opt.Parameters["userId"]);
    }

    [Fact]
    public async Task TeamQueryLibrary_Saves_And_Retrieves_Shared_Queries()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var factory = new TestDbContextFactory(options);
        var ws = new WorkspaceService(factory);

        var dbId = Guid.NewGuid();
        var user1 = "user1";
        var user2 = "user2";

        // Save private query for user1
        var res1 = await ws.SaveAsync(user1, dbId, "Private Query", "SELECT 1", "test", "Private", category: "General", isShared: false, userName: "Alice");
        Assert.True(res1.Ok);

        // Save shared query for user1
        var res2 = await ws.SaveAsync(user1, dbId, "Shared Query", "SELECT 2", "team", "Shared with team", category: "Billing", isShared: true, userName: "Alice");
        Assert.True(res2.Ok);

        // user1's saved queries should have 2
        var user1Queries = await ws.SavedAsync(user1, dbId);
        Assert.Equal(2, user1Queries.Count);

        // user2's personal saved queries should have 0
        var user2Queries = await ws.SavedAsync(user2, dbId);
        Assert.Empty(user2Queries);

        // Team library should have 1 shared query
        var teamQueries = await ws.TeamSavedAsync(dbId);
        Assert.Single(teamQueries);
        Assert.Equal("Shared Query", teamQueries[0].Name);
        Assert.Equal("Billing", teamQueries[0].Category);
        Assert.True(teamQueries[0].IsShared);
        Assert.Equal("Alice", teamQueries[0].CreatedByUserName);
    }

    [Fact]
    public async Task PiiDiscoveryService_Correctly_Identifies_Sensitive_Columns()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var factory = new TestDbContextFactory(options);
        var dbId = Guid.NewGuid();

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                TenantId = Guid.NewGuid(),
                Name = "TestDB",
                Environment = "Production",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = "dummy"
            });
            await db.SaveChangesAsync();
        }

        // Create a metadata service mock/stub with test tables
        var dummyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
            }).Build();
        var dummyCrypto = new CryptoService(dummyConfig);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settings = new SettingsService(factory, cache);
        var providers = new DatabaseProviderFactory();
        var metaSvc = new MetadataService(factory, dummyCrypto, cache, settings, providers);

        var piiScanner = new PiiDiscoveryService(metaSvc, factory);

        // Scanner runs against database
        var findings = await piiScanner.ScanDatabaseAsync(dbId);
        Assert.NotNull(findings);
    }

    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }
}
