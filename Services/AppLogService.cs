using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services;

public sealed class AppLogService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<AppLogService> logger)
{
    public async Task<long> WriteAsync(AppLog entry)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (entry.TenantId == Guid.Empty)
        {
            entry.TenantId = db.CurrentTenantId ?? TenantContextAccessor.FallbackTenantId;
        }
        entry.Message = Truncate(entry.Message, 2000) ?? "";
        entry.Level = Truncate(entry.Level, 32) ?? "Information";
        entry.CorrelationId = Truncate(entry.CorrelationId, 128);
        entry.UserName = Truncate(entry.UserName, 256);
        entry.Endpoint = Truncate(entry.Endpoint, 512);

        db.AppLogs.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    public async Task TryWriteAsync(AppLog entry)
    {
        try
        {
            await WriteAsync(entry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write application log entry: {Message}", entry.Message);
        }
    }

    public Task LogErrorAsync(string message, Exception? ex = null, string? userName = null, string? endpoint = null, Guid? tenantId = null, string? correlationId = null) =>
        TryWriteAsync(new AppLog
        {
            Level = "Error",
            Message = message,
            Exception = ex?.ToString(),
            UserName = userName,
            Endpoint = endpoint,
            CorrelationId = correlationId,
            TenantId = tenantId ?? Guid.Empty
        });

    public Task LogWarningAsync(string message, Exception? ex = null, string? userName = null, string? endpoint = null, Guid? tenantId = null, string? correlationId = null) =>
        TryWriteAsync(new AppLog
        {
            Level = "Warning",
            Message = message,
            Exception = ex?.ToString(),
            UserName = userName,
            Endpoint = endpoint,
            CorrelationId = correlationId,
            TenantId = tenantId ?? Guid.Empty
        });

    public Task LogInfoAsync(string message, string? userName = null, string? endpoint = null, Guid? tenantId = null, string? correlationId = null) =>
        TryWriteAsync(new AppLog
        {
            Level = "Information",
            Message = message,
            UserName = userName,
            Endpoint = endpoint,
            CorrelationId = correlationId,
            TenantId = tenantId ?? Guid.Empty
        });

    public static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
