using System.Collections.Concurrent;
using DbAccessManager.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DbAccessManager.Services;

public sealed record CachedDatabaseDescriptor(
    Guid Id,
    Guid TenantId,
    string Name,
    string Environment,
    DatabaseEngine Engine,
    bool IsActive,
    int DefaultTimeoutSeconds,
    int MaxRows,
    string? IncludedObjects,
    string? ExcludedObjects,
    string DecryptedConnectionString);

public interface IDatabaseCache
{
    Task<CachedDatabaseDescriptor?> GetAsync(Guid databaseId);
    void Invalidate(Guid? databaseId = null);
}

public sealed class DatabaseCache(
    IDbContextFactory<ApplicationDbContext> factory,
    CryptoService crypto,
    IMemoryCache? cache = null) : IDatabaseCache
{
    private readonly ConcurrentDictionary<Guid, byte> _tracked = new();

    public async Task<CachedDatabaseDescriptor?> GetAsync(Guid databaseId)
    {
        var key = "managed_db_desc:" + databaseId;
        if (cache != null && cache.TryGetValue(key, out CachedDatabaseDescriptor? cached) && cached != null)
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync();
        var d = await db.ManagedDatabases.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == databaseId);
        if (d is null) return null;

        var decrypted = crypto.Decrypt(d.EncryptedConnectionString);
        var descriptor = new CachedDatabaseDescriptor(
            d.Id,
            d.TenantId,
            d.Name,
            d.Environment,
            d.Engine,
            d.IsActive,
            d.DefaultTimeoutSeconds,
            d.MaxRows,
            d.IncludedObjects,
            d.ExcludedObjects,
            decrypted);

        if (cache != null)
        {
            cache.Set(key, descriptor, TimeSpan.FromMinutes(30));
            _tracked.TryAdd(databaseId, 0);
        }

        return descriptor;
    }

    public void Invalidate(Guid? databaseId = null)
    {
        if (cache == null) return;
        if (databaseId.HasValue)
        {
            cache.Remove("managed_db_desc:" + databaseId.Value);
            _tracked.TryRemove(databaseId.Value, out _);
        }
        else
        {
            foreach (var id in _tracked.Keys)
            {
                cache.Remove("managed_db_desc:" + id);
            }
            _tracked.Clear();
        }
    }
}
