using System.Threading.Channels;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services;

public sealed class AppDbLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private static readonly AsyncLocal<bool> _isWritingLog = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Channel<AppLog> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private IExternalScopeProvider? _scopeProvider;

    public AppDbLoggerProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _channel = Channel.CreateBounded<AppLog>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new AppDbLogger(categoryName, _channel.Writer, () => _scopeProvider);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var batch = new List<AppLog>(50);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < 50 && _channel.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count == 0) continue;

                // If more items are trickling in right after a burst, coalesce briefly up to 250ms.
                if (batch.Count < 50)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    delayCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                    try
                    {
                        while (batch.Count < 50 && await _channel.Reader.WaitToReadAsync(delayCts.Token))
                        {
                            while (batch.Count < 50 && _channel.Reader.TryRead(out var extra))
                            {
                                batch.Add(extra);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }

                await FlushBatchAsync(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining queued entries on shutdown
            batch.Clear();
            while (_channel.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
                if (batch.Count >= 50)
                {
                    await FlushBatchAsync(batch);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch);
            }
        }
    }

    private async Task FlushBatchAsync(List<AppLog> batch)
    {
        _isWritingLog.Value = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetService<IDbContextFactory<ApplicationDbContext>>();
            if (dbFactory is null) return;

            await using var db = await dbFactory.CreateDbContextAsync();
            var fallbackTenant = db.CurrentTenantId ?? TenantContextAccessor.FallbackTenantId;
            foreach (var entry in batch)
            {
                if (entry.TenantId == Guid.Empty)
                {
                    entry.TenantId = fallbackTenant;
                }
                entry.Message = AppLogService.Truncate(entry.Message, 2000) ?? "";
                entry.Level = AppLogService.Truncate(entry.Level, 32) ?? "Information";
                entry.CorrelationId = AppLogService.Truncate(entry.CorrelationId, 128);
                entry.UserName = AppLogService.Truncate(entry.UserName, 256);
                entry.Endpoint = AppLogService.Truncate(entry.Endpoint, 512);
            }

            db.AppLogs.AddRange(batch);
            await db.SaveChangesAsync();
        }
        catch
        {
            // Fail-safe: never crash application due to logger failure
        }
        finally
        {
            _isWritingLog.Value = false;
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private sealed class AppDbLogger(
        string categoryName,
        ChannelWriter<AppLog> writer,
        Func<IExternalScopeProvider?> scopeProviderAccessor) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            scopeProviderAccessor()?.Push(state);

        public bool IsEnabled(LogLevel logLevel)
        {
            if (_isWritingLog.Value) return false;
            // Ignore EF Core internal command execution logs to avoid cycles
            if (categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)) return false;
            if (categoryName.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)) return false;
            if (logLevel >= LogLevel.Error) return true;
            if (logLevel == LogLevel.Warning && categoryName.StartsWith("DbAccessManager", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (_isWritingLog.Value) return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null) return;

            var level = logLevel switch
            {
                LogLevel.Critical => "Critical",
                LogLevel.Error => "Error",
                LogLevel.Warning => "Warning",
                _ => "Information"
            };

            writer.TryWrite(new Data.AppLog
            {
                Level = level,
                Message = string.IsNullOrWhiteSpace(message) ? (exception?.Message ?? "Logged error") : message,
                Exception = exception?.ToString(),
                Endpoint = categoryName
            });
        }
    }
}
