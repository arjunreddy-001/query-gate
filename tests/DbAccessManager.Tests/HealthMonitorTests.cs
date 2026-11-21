using DbAccessManager.Data;
using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class HealthMonitorTests
{
    [Fact]
    public void HealthResult_Properties_Are_Preserved()
    {
        var dbId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var result = new HealthResult(
            dbId,
            tenantId,
            "OrdersDb",
            "Production",
            DatabaseEngine.SqlServer,
            true,
            "Connected successfully",
            12,
            now);

        Assert.Equal(dbId, result.DatabaseId);
        Assert.Equal(tenantId, result.TenantId);
        Assert.Equal("OrdersDb", result.DatabaseName);
        Assert.Equal("Production", result.Environment);
        Assert.Equal(DatabaseEngine.SqlServer, result.Engine);
        Assert.True(result.IsOnline);
        Assert.Equal("Connected successfully", result.Message);
        Assert.Equal(12, result.LatencyMs);
        Assert.Equal(now, result.CheckedAt);
    }

    [Fact]
    public void WebhookService_Includes_Health_EventFilter()
    {
        Assert.Contains("Health", WebhookService.EventFilters);
        var label = WebhookService.EventFilterLabel("Health");
        Assert.Equal("Database health alerts only", label);
    }

    [Fact]
    public void SettingsService_Definitions_Contains_HealthVisibleRoles()
    {
        Assert.True(SettingsService.Definitions.ContainsKey("HealthVisibleRoles"));
        var (defVal, label) = SettingsService.Definitions["HealthVisibleRoles"];
        Assert.Equal("", defVal);
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    [Fact]
    public void HealthResult_Offline_State_Preserved()
    {
        var dbId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var result = new HealthResult(
            dbId,
            tenantId,
            "BrokenDb",
            "Development",
            DatabaseEngine.PostgreSql,
            false,
            "Connection refused",
            504,
            now);

        Assert.False(result.IsOnline);
        Assert.Equal("Connection refused", result.Message);
        Assert.Equal(504, result.LatencyMs);
    }

    [Fact]
    public void TargetTelemetry_Properties_Are_Preserved()
    {
        var telem = new DbAccessManager.Services.Providers.TargetTelemetry(
            ActiveConnections: 14,
            MaxConnections: 100,
            DatabaseSize: "256.4 MB",
            CacheHitRatio: 99.2,
            BlockedQueriesCount: 0,
            InFlightQueries: 2,
            AuditTotal24h: 150,
            AuditErrorRate24h: 1.3,
            AuditP95Ms24h: 45);

        Assert.Equal(14, telem.ActiveConnections);
        Assert.Equal(100, telem.MaxConnections);
        Assert.Equal("256.4 MB", telem.DatabaseSize);
        Assert.Equal(99.2, telem.CacheHitRatio);
        Assert.Equal(0, telem.BlockedQueriesCount);
        Assert.Equal(2, telem.InFlightQueries);
        Assert.Equal(150, telem.AuditTotal24h);
        Assert.Equal(1.3, telem.AuditErrorRate24h);
        Assert.Equal(45, telem.AuditP95Ms24h);

        var result = new HealthResult(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TeleDb",
            "Production",
            DatabaseEngine.MySql,
            true,
            "Connected",
            10,
            DateTimeOffset.UtcNow,
            telem);

        Assert.NotNull(result.Telemetry);
        Assert.Equal("256.4 MB", result.Telemetry.DatabaseSize);
    }
}
