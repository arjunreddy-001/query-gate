using System.Collections.Concurrent;
using System.Diagnostics;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services;

public sealed record HealthResult(
    Guid DatabaseId,
    Guid TenantId,
    string DatabaseName,
    string Environment,
    DatabaseEngine Engine,
    bool IsOnline,
    string Message,
    long LatencyMs,
    DateTimeOffset CheckedAt,
    TargetTelemetry? Telemetry = null);

public sealed class HealthMonitorService(
    IDbContextFactory<ApplicationDbContext> factory,
    CryptoService crypto,
    DatabaseProviderFactory providers,
    NotificationService notifications,
    WebhookService webhooks,
    ILogger<HealthMonitorService> logger,
    ExecutionService? executionService = null) : BackgroundService
{
    private const int PollIntervalSeconds = 60;
    private readonly ConcurrentDictionary<Guid, HealthResult> _results = new();
    private readonly ConcurrentDictionary<Guid, byte> _probing = new();

    public IReadOnlyList<HealthResult> GetAll(Guid? tenantId = null)
    {
        var items = _results.Values.AsEnumerable();
        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            items = items.Where(r => r.TenantId == tenantId.Value);
        }
        return items.OrderBy(r => r.Environment).ThenBy(r => r.DatabaseName).ToList();
    }

    public HealthResult? Get(Guid databaseId) => _results.GetValueOrDefault(databaseId);

    public async Task<HealthResult?> CheckNowAsync(Guid databaseId, CancellationToken ct = default)
    {
        if (!_probing.TryAdd(databaseId, 0))
        {
            return _results.GetValueOrDefault(databaseId);
        }

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var managedDb = await db.ManagedDatabases.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId, ct);
            if (managedDb is null || !managedDb.IsActive)
            {
                _results.TryRemove(databaseId, out _);
                return null;
            }

            return await ProbeOneAsync(managedDb, ct);
        }
        finally
        {
            _probing.TryRemove(databaseId, out _);
        }
    }

    public async Task CheckAllAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ManagedDatabases.IgnoreQueryFilters().AsNoTracking().Where(d => d.IsActive);
        if (tenantId.HasValue && tenantId.Value != Guid.Empty)
        {
            query = query.Where(d => d.TenantId == tenantId.Value);
        }
        var list = await query.ToListAsync(ct);
        await Task.WhenAll(list.Select(d => ProbeOneAsync(d, ct)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(5_000, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during health monitor background cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeAllAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var activeDatabases = await db.ManagedDatabases.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.IsActive)
            .ToListAsync(ct);

        var activeIds = activeDatabases.Select(d => d.Id).ToHashSet();
        foreach (var key in _results.Keys)
        {
            if (!activeIds.Contains(key))
            {
                _results.TryRemove(key, out _);
            }
        }

        await Task.WhenAll(activeDatabases.Select(d => ProbeOneAsync(d, ct)));
    }

    private async Task<HealthResult> ProbeOneAsync(ManagedDatabase d, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var prev = _results.GetValueOrDefault(d.Id);
        bool ok;
        string message;
        TargetTelemetry? telemetry = null;
        try
        {
            var connStr = crypto.Decrypt(d.EncryptedConnectionString);
            var provider = providers.GetProvider(d.Engine);
            (ok, message) = await provider.TestAsync(connStr);
            if (ok)
            {
                var targetTelem = await provider.CollectTelemetryAsync(connStr, ct);
                var auditStats = await CalculateAuditStatsAsync(d.Id, ct);
                var inFlight = executionService?.GetInFlightQueryCount(d.Id) ?? 0;

                telemetry = targetTelem with
                {
                    InFlightQueries = inFlight,
                    AuditTotal24h = auditStats.Total,
                    AuditErrorRate24h = auditStats.ErrorRate,
                    AuditP95Ms24h = auditStats.P95Ms
                };
            }
        }
        catch (Exception ex)
        {
            ok = false;
            message = ex.Message;
        }
        sw.Stop();

        var result = new HealthResult(
            d.Id,
            d.TenantId,
            d.Name,
            d.Environment,
            d.Engine,
            ok,
            message,
            sw.ElapsedMilliseconds,
            DateTimeOffset.UtcNow,
            telemetry);

        _results[d.Id] = result;

        if (prev is not null && prev.IsOnline != ok)
        {
            OnStateTransition(d, prev.IsOnline, ok, message, sw.ElapsedMilliseconds);
        }

        return result;
    }

    private async Task<(int Total, double ErrorRate, long P95Ms)> CalculateAuditStatsAsync(Guid databaseId, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var entries = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.DatabaseId == databaseId && a.Timestamp >= since && (a.Category == "Query" || a.Category == "ScriptExecution" || a.Category == "Execution"))
                .Select(a => new { a.Outcome, a.DurationMs })
                .ToListAsync(ct);

            if (entries.Count == 0) return (0, 0, 0);

            var total = entries.Count;
            var errorCount = entries.Count(e => e.Outcome != "Success");
            var errorRate = Math.Round((double)errorCount / total * 100.0, 1);

            var durations = entries.Where(e => e.DurationMs.HasValue).Select(e => e.DurationMs!.Value).OrderBy(x => x).ToList();
            long p95 = 0;
            if (durations.Count > 0)
            {
                var idx = (int)Math.Ceiling(durations.Count * 0.95) - 1;
                idx = Math.Clamp(idx, 0, durations.Count - 1);
                p95 = durations[idx];
            }

            return (total, errorRate, p95);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to calculate audit stats for database {Id}", databaseId);
            return (0, 0, 0);
        }
    }

    private void OnStateTransition(ManagedDatabase d, bool wasOnline, bool isOnline, string message, long latencyMs)
    {
        var title = isOnline
            ? $"Database {d.Name} is back online"
            : $"Database {d.Name} went offline";

        var msg = isOnline
            ? $"{d.Name} ({d.Environment}) is reachable. Latency: {latencyMs} ms."
            : $"{d.Name} ({d.Environment}) connection failed: {message}";

        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var tenantAdmins = await (
                    from m in db.UserTenantMemberships.IgnoreQueryFilters()
                    join u in db.Users on m.UserId equals u.Id
                    where m.TenantId == d.TenantId && m.Role == Roles.Admin && u.IsActive
                    select m.UserId).ToListAsync();

                var superAdmins = await (
                    from ur in db.UserRoles
                    join r in db.Roles on ur.RoleId equals r.Id
                    join u in db.Users on ur.UserId equals u.Id
                    where r.Name == Roles.SuperAdmin && u.IsActive
                    select ur.UserId).ToListAsync();

                var allTargetUserIds = tenantAdmins.Concat(superAdmins).Distinct().ToList();
                var isFirst = true;
                foreach (var uid in allTargetUserIds)
                {
                    await notifications.NotifyAsync(uid, title, msg, "/admin/databases", broadcastWebhook: isFirst);
                    isFirst = false;
                }

                if (allTargetUserIds.Count == 0)
                {
                    await webhooks.BroadcastAsync("Health", title, msg, "/admin/databases");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send state change notification for database {Name}", d.Name);
            }
        });
    }
}
