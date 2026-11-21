using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace DbAccessManager.Security;

public sealed record CachedTenant(Guid Id, string Slug, string Name, bool IsActive);

public interface ITenantLookupCache
{
    bool TryGetBySlug(string slug, out CachedTenant? tenant);
    void SetBySlug(string slug, CachedTenant? tenant);
    bool TryGetByDomain(string domain, out CachedTenant? tenant);
    void SetByDomain(string domain, CachedTenant? tenant);
    bool TryGetSingleActive(out bool isSingle, out CachedTenant? tenant);
    void SetSingleActive(bool isSingle, CachedTenant? tenant);
    void Invalidate();
}

public sealed class TenantLookupCache(IMemoryCache? cache = null) : ITenantLookupCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, byte> _trackedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (CachedTenant? Tenant, DateTimeOffset ExpiresAt)> _fallbackCache = new(StringComparer.OrdinalIgnoreCase);
    private (bool Resolved, bool IsSingle, CachedTenant? Tenant, DateTimeOffset ExpiresAt) _fallbackSingleActive;

    public bool TryGetBySlug(string slug, out CachedTenant? tenant)
    {
        var key = "tenant:slug:" + slug.ToLowerInvariant();
        if (cache != null)
        {
            if (cache.TryGetValue(key, out (bool HasValue, CachedTenant? Tenant) cached))
            {
                tenant = cached.Tenant;
                return true;
            }
            tenant = null;
            return false;
        }

        if (_fallbackCache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            tenant = entry.Tenant;
            return true;
        }

        tenant = null;
        return false;
    }

    public void SetBySlug(string slug, CachedTenant? tenant)
    {
        var key = "tenant:slug:" + slug.ToLowerInvariant();
        var ttl = tenant is not null ? DefaultTtl : NegativeTtl;
        if (cache != null)
        {
            cache.Set(key, (HasValue: true, Tenant: tenant), ttl);
            _trackedKeys.TryAdd(key, 0);
        }
        else
        {
            _fallbackCache[key] = (tenant, DateTimeOffset.UtcNow.Add(ttl));
        }
    }

    public bool TryGetByDomain(string domain, out CachedTenant? tenant)
    {
        var key = "tenant:domain:" + domain.ToLowerInvariant();
        if (cache != null)
        {
            if (cache.TryGetValue(key, out (bool HasValue, CachedTenant? Tenant) cached))
            {
                tenant = cached.Tenant;
                return true;
            }
            tenant = null;
            return false;
        }

        if (_fallbackCache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            tenant = entry.Tenant;
            return true;
        }

        tenant = null;
        return false;
    }

    public void SetByDomain(string domain, CachedTenant? tenant)
    {
        var key = "tenant:domain:" + domain.ToLowerInvariant();
        var ttl = tenant is not null ? DefaultTtl : NegativeTtl;
        if (cache != null)
        {
            cache.Set(key, (HasValue: true, Tenant: tenant), ttl);
            _trackedKeys.TryAdd(key, 0);
        }
        else
        {
            _fallbackCache[key] = (tenant, DateTimeOffset.UtcNow.Add(ttl));
        }
    }

    public bool TryGetSingleActive(out bool isSingle, out CachedTenant? tenant)
    {
        const string key = "tenant:single_active";
        if (cache != null)
        {
            if (cache.TryGetValue(key, out (bool Resolved, bool IsSingle, CachedTenant? Tenant) cached))
            {
                isSingle = cached.IsSingle;
                tenant = cached.Tenant;
                return true;
            }
            isSingle = false;
            tenant = null;
            return false;
        }

        if (_fallbackSingleActive.Resolved && _fallbackSingleActive.ExpiresAt > DateTimeOffset.UtcNow)
        {
            isSingle = _fallbackSingleActive.IsSingle;
            tenant = _fallbackSingleActive.Tenant;
            return true;
        }

        isSingle = false;
        tenant = null;
        return false;
    }

    public void SetSingleActive(bool isSingle, CachedTenant? tenant)
    {
        const string key = "tenant:single_active";
        if (cache != null)
        {
            cache.Set(key, (Resolved: true, IsSingle: isSingle, Tenant: tenant), DefaultTtl);
            _trackedKeys.TryAdd(key, 0);
        }
        else
        {
            _fallbackSingleActive = (true, isSingle, tenant, DateTimeOffset.UtcNow.Add(DefaultTtl));
        }
    }

    public void Invalidate()
    {
        if (cache != null)
        {
            foreach (var key in _trackedKeys.Keys)
            {
                cache.Remove(key);
            }
            cache.Remove("tenant:single_active");
            _trackedKeys.Clear();
        }
        _fallbackCache.Clear();
        _fallbackSingleActive = default;
    }
}
