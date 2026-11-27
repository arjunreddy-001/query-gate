using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace DbAccessManager.Services;

public sealed record ExportDownloadEntry(byte[] Bytes, string ContentType, string FileName, string UserId);

public sealed class ExportDownloadCache(IMemoryCache? cache = null)
{
    private readonly ConcurrentDictionary<string, (ExportDownloadEntry Entry, DateTimeOffset ExpiresAt)> _fallback = new();

    public string Register(byte[] bytes, string contentType, string fileName, string userId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var entry = new ExportDownloadEntry(bytes, contentType, fileName, userId);

        if (cache is not null)
        {
            cache.Set(CacheKey(token), entry, TimeSpan.FromSeconds(60));
        }
        else
        {
            PruneFallback();
            _fallback[token] = (entry, DateTimeOffset.UtcNow.AddSeconds(60));
        }

        return token;
    }

    public bool TryTake(string token, string userId, out ExportDownloadEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (cache is not null)
        {
            var key = CacheKey(token);
            if (cache.TryGetValue(key, out ExportDownloadEntry? cached) && cached is not null)
            {
                if (!string.Equals(cached.UserId, userId, StringComparison.Ordinal))
                {
                    return false;
                }
                cache.Remove(key);
                entry = cached;
                return true;
            }
            return false;
        }

        if (_fallback.TryGetValue(token, out var item))
        {
            if (item.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _fallback.TryRemove(token, out _);
                return false;
            }
            if (!string.Equals(item.Entry.UserId, userId, StringComparison.Ordinal))
            {
                return false;
            }
            _fallback.TryRemove(token, out _);
            entry = item.Entry;
            return true;
        }

        return false;
    }

    private void PruneFallback()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _fallback)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _fallback.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string CacheKey(string token) => "export_dl:" + token;
}
