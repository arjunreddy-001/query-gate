using System.Net;
using System.Text;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Ai;
using DbAccessManager.Services.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class AiAssistantServiceTests
{
    private sealed class AiTestContext
    {
        public IDbContextFactory<ApplicationDbContext> Factory { get; }
        public CryptoService Crypto { get; }
        public SettingsService Settings { get; }
        public MetadataService Metadata { get; }
        public SqlGate Gate { get; }
        public AuditService Audit { get; }
        public AiAssistantService Service { get; }
        public TestHttpClientFactory HttpFactory { get; }

        public AiTestContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase("AiAssistantTests_" + Guid.NewGuid())
                .Options;
            Factory = new SimpleDbContextFactory(options);

            var inMemorySettings = new Dictionary<string, string?>
            {
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            Crypto = new CryptoService(config);

            var memCache = new MemoryCache(new MemoryCacheOptions());
            var tenantAccessor = new TenantContextAccessor();
            Settings = new SettingsService(Factory, memCache, tenantAccessor);
            Metadata = new MetadataService(Factory, Crypto, memCache, Settings, new DatabaseProviderFactory());
            Gate = new SqlGate();
            Audit = new AuditService(Factory, NullLogger<AuditService>.Instance);
            HttpFactory = new TestHttpClientFactory();

            Service = new AiAssistantService(
                HttpFactory,
                Settings,
                Crypto,
                Metadata,
                Gate,
                Audit,
                NullLogger<AiAssistantService>.Instance);
        }
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

        public HttpClient CreateClient(string name)
        {
            var handler = new MockHttpMessageHandler(Response);
            return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        }
    }

    private sealed class MockHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalseWhenDisabled()
    {
        var ctx = new AiTestContext();
        var configured = await ctx.Service.IsConfiguredAsync();
        Assert.False(configured);
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsTrueForOllamaWithEndpoint()
    {
        var ctx = new AiTestContext();
        await ctx.Service.SaveAiConfigAsync(
            enabled: true,
            provider: "Ollama",
            endpoint: "http://localhost:11434/v1",
            rawApiKey: "",
            model: "llama3.2",
            temperature: 0.1);

        var configured = await ctx.Service.IsConfiguredAsync();
        Assert.True(configured);
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsTrueForOpenAiWithApiKey()
    {
        var ctx = new AiTestContext();
        await ctx.Service.SaveAiConfigAsync(
            enabled: true,
            provider: "OpenAI",
            endpoint: "https://api.openai.com/v1",
            rawApiKey: "sk-test-mock-api-key-12345",
            model: "gpt-4o-mini",
            temperature: 0.2);

        var configured = await ctx.Service.IsConfiguredAsync();
        Assert.True(configured);

        var decryptedKey = await ctx.Service.GetDecryptedApiKeyAsync();
        Assert.Equal("sk-test-mock-api-key-12345", decryptedKey);
    }

    [Fact]
    public async Task GenerateSqlAsync_ReturnsUnconfiguredError_WhenDisabled()
    {
        var ctx = new AiTestContext();
        var result = await ctx.Service.GenerateSqlAsync(Guid.NewGuid(), DatabaseEngine.SqlServer, "Show all users");

        Assert.False(result.Success);
        Assert.Null(result.Sql);
        Assert.Contains("AI Assistant is not configured", result.ErrorMessage);
    }

    [Fact]
    public async Task ExplainOrFixAsync_ReturnsUnconfiguredError_WhenDisabled()
    {
        var ctx = new AiTestContext();
        var result = await ctx.Service.ExplainOrFixAsync(Guid.NewGuid(), DatabaseEngine.SqlServer, "SELECT * FROM Missing", "Table not found");

        Assert.False(result.Success);
        Assert.Contains("AI Assistant is not configured", result.ErrorMessage);
    }

    [Fact]
    public async Task ExplainPlanAsync_ReturnsUnconfiguredMessage_WhenDisabled()
    {
        var ctx = new AiTestContext();
        var plan = new ExecutionPlanDto
        {
            TotalCost = 1.23,
            EstimatedRows = 100,
            OptimizationLevel = "FULL"
        };
        var explanation = await ctx.Service.ExplainPlanAsync(plan, DatabaseEngine.SqlServer);

        Assert.Contains("AI Assistant is not configured", explanation);
    }

    [Fact]
    public async Task BuildCompactSchemaContextAsync_FormatsTablesAndForeignKeys()
    {
        var ctx = new AiTestContext();
        var dbId = Guid.NewGuid();

        // Seed database
        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "TestDb",
                Environment = "Production",
                Engine = DatabaseEngine.SqlServer,
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=TestDb;"),
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        // Fake metadata cache entry
        var obj1 = new DbObjectInfo("dbo", "Customers", "TABLE", ["Id", "Name"]);
        obj1.ColumnDetails.Add(new DbColumnInfo("Id", "int", false, IsIdentity: true));
        obj1.ColumnDetails.Add(new DbColumnInfo("Name", "nvarchar(100)", false));

        var obj2 = new DbObjectInfo("dbo", "Orders", "TABLE", ["Id", "CustomerId", "Total"]);
        obj2.ColumnDetails.Add(new DbColumnInfo("Id", "int", false, IsIdentity: true));
        obj2.ColumnDetails.Add(new DbColumnInfo("CustomerId", "int", false));
        obj2.ColumnDetails.Add(new DbColumnInfo("Total", "decimal(18,2)", false));
        obj2.ForeignKeys.Add(new DbForeignKeyInfo("FK_Orders_Customers", "dbo", "Customers", [new DbForeignKeyColumn("CustomerId", "Id")]));

        // Pre-populate memory cache to mock metadata provider call
        var memCache = new MemoryCache(new MemoryCacheOptions());
        memCache.Set("meta:" + dbId, new List<DbObjectInfo> { obj1, obj2 });
        var metadataService = new MetadataService(ctx.Factory, ctx.Crypto, memCache, ctx.Settings, new DatabaseProviderFactory());

        var customAiService = new AiAssistantService(
            ctx.HttpFactory,
            ctx.Settings,
            ctx.Crypto,
            metadataService,
            ctx.Gate,
            ctx.Audit,
            NullLogger<AiAssistantService>.Instance);

        var schemaContext = await customAiService.BuildCompactSchemaContextAsync(dbId);

        Assert.Contains("TABLE dbo.Customers (Id int IDENTITY, Name nvarchar(100))", schemaContext);
        Assert.Contains("TABLE dbo.Orders (Id int IDENTITY, CustomerId int, Total decimal(18,2))", schemaContext);
        Assert.Contains("FK: dbo.Orders.CustomerId -> dbo.Customers.Id", schemaContext);
    }
}
