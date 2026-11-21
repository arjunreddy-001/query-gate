using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services;

public sealed class AppDbLoggerProvider(IServiceScopeFactory scopeFactory) : ILoggerProvider, ISupportExternalScope
{
    private static readonly AsyncLocal<bool> _isWritingLog = new();
    private IExternalScopeProvider? _scopeProvider;

    public ILogger CreateLogger(string categoryName)
    {
        return new AppDbLogger(categoryName, scopeFactory, () => _scopeProvider);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public void Dispose() { }

    private sealed class AppDbLogger(
        string categoryName,
        IServiceScopeFactory scopeFactory,
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

            // Fire-and-forget in background Task
            _ = Task.Run(async () =>
            {
                _isWritingLog.Value = true;
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var appLogService = scope.ServiceProvider.GetService<AppLogService>();
                    if (appLogService is null) return;

                    var level = logLevel switch
                    {
                        LogLevel.Critical => "Critical",
                        LogLevel.Error => "Error",
                        LogLevel.Warning => "Warning",
                        _ => "Information"
                    };

                    await appLogService.TryWriteAsync(new Data.AppLog
                    {
                        Level = level,
                        Message = string.IsNullOrWhiteSpace(message) ? (exception?.Message ?? "Logged error") : message,
                        Exception = exception?.ToString(),
                        Endpoint = categoryName
                    });
                }
                catch
                {
                    // Fail-safe: never crash application due to logger failure
                }
                finally
                {
                    _isWritingLog.Value = false;
                }
            });
        }
    }
}
