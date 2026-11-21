using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class AppLogServiceTests
{
    private static ServiceProvider CreateServices(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(dbName)
                   .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<ITenantContext, TenantContext>();
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<AppLogService>();
        services.AddSingleton<AccessConfigurationService>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task WriteAsync_PersistsLogEntryAndTruncatesFields()
    {
        var dbName = "AppLogServiceTests_" + Guid.NewGuid();
        var services = CreateServices(dbName);
        var appLog = services.GetRequiredService<AppLogService>();
        var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        var veryLongMessage = new string('A', 3000);
        var longCorrelation = new string('B', 200);
        var testEx = new InvalidOperationException("Test exception message");

        await appLog.LogErrorAsync(
            message: veryLongMessage,
            ex: testEx,
            userName: "admin@example.com",
            endpoint: "/admin/permissions",
            correlationId: longCorrelation);

        await using var db = await factory.CreateDbContextAsync();
        var entry = await db.AppLogs.IgnoreQueryFilters().FirstOrDefaultAsync();

        Assert.NotNull(entry);
        Assert.Equal("Error", entry.Level);
        Assert.Equal(2000, entry.Message.Length);
        Assert.Equal(128, entry.CorrelationId?.Length);
        Assert.Equal("admin@example.com", entry.UserName);
        Assert.Equal("/admin/permissions", entry.Endpoint);
        Assert.NotNull(entry.Exception);
        Assert.Contains("Test exception message", entry.Exception);
    }

    [Fact]
    public async Task SaveEnvironmentAsync_HandlesAddAndDuplicateGracefully()
    {
        var dbName = "SaveEnvironmentTests_" + Guid.NewGuid();
        var services = CreateServices(dbName);
        var access = services.GetRequiredService<AccessConfigurationService>();
        var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var actor = new Actor("u1", "admin", "127.0.0.1", "TestAgent", Guid.NewGuid());

        // First save should succeed
        var result1 = await access.SaveEnvironmentAsync(actor, null, "Prod");
        Assert.True(result1.Ok);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var env = await db.DatabaseEnvironments.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Name == "Prod");
            Assert.NotNull(env);
            Assert.Equal("Prod", env.Name);
            Assert.Equal(actor.TenantId, env.TenantId);
        }

        // Duplicate with same case should return Fail instead of throwing exception
        var result2 = await access.SaveEnvironmentAsync(actor, null, "Prod");
        Assert.False(result2.Ok);
        Assert.Contains("already exists", result2.Error, StringComparison.OrdinalIgnoreCase);

        // Duplicate with different case should also return Fail instead of throwing exception
        var result3 = await access.SaveEnvironmentAsync(actor, null, "prod");
        Assert.False(result3.Ok);
        Assert.Contains("already exists", result3.Error, StringComparison.OrdinalIgnoreCase);
    }
}
