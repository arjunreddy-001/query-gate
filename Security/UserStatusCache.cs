using System.Collections.Concurrent;
using DbAccessManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace DbAccessManager.Security;

public sealed record CachedUserStatus(
    string Id,
    string Email,
    bool IsActive,
    DateTimeOffset? ExpiresAt,
    int? TimeoutOverrideSeconds,
    string? SecurityStamp)
{
    public bool CanSignIn(DateTimeOffset now) => IsActive && (!ExpiresAt.HasValue || ExpiresAt.Value > now);
}

public interface IUserStatusCache
{
    Task<CachedUserStatus?> GetOrLoadAsync(string userId, UserManager<ApplicationUser> userManager);
    void Invalidate(string? userId = null);
}

public sealed class UserStatusCache(IMemoryCache? cache = null) : IUserStatusCache
{
    private readonly ConcurrentDictionary<string, byte> _tracked = new(StringComparer.OrdinalIgnoreCase);

    public async Task<CachedUserStatus?> GetOrLoadAsync(string userId, UserManager<ApplicationUser> userManager)
    {
        var key = "user_status:" + userId;
        if (cache != null && cache.TryGetValue(key, out (bool HasValue, CachedUserStatus? Status) cached))
        {
            return cached.Status;
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            if (cache != null)
            {
                cache.Set(key, (HasValue: true, Status: (CachedUserStatus?)null), TimeSpan.FromSeconds(30));
                _tracked.TryAdd(userId, 0);
            }
            return null;
        }

        var status = new CachedUserStatus(
            user.Id,
            user.Email ?? "",
            user.IsActive,
            user.ExpiresAt,
            user.TimeoutOverrideSeconds,
            user.SecurityStamp);

        if (cache != null)
        {
            cache.Set(key, (HasValue: true, Status: status), TimeSpan.FromMinutes(2));
            _tracked.TryAdd(userId, 0);
        }

        return status;
    }

    public void Invalidate(string? userId = null)
    {
        if (cache == null) return;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            cache.Remove("user_status:" + userId);
            _tracked.TryRemove(userId, out _);
        }
        else
        {
            foreach (var id in _tracked.Keys)
            {
                cache.Remove("user_status:" + id);
            }
            _tracked.Clear();
        }
    }
}
