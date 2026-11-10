using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace DbAccessManager.Services;

public sealed class DatabaseAdminService(IDbContextFactory<ApplicationDbContext> factory, CryptoService crypto, AuditService audit, DatabaseProviderFactory providers)
{
    public async Task<(bool Ok, string Message)> TestAsync(string connectionString, DatabaseEngine engine = DatabaseEngine.SqlServer)
    {
        var provider = providers.GetProvider(engine);
        return await provider.TestAsync(connectionString);
    }

    public async Task<(bool Ok, string Message)> TestRegisteredAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var database = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (database is null) return (false, "Database not found.");
        return await TestAsync(crypto.Decrypt(database.EncryptedConnectionString), database.Engine);
    }

    public async Task<OpResult> RegisterAsync(Actor actor, string name, string environment, string? description, string connectionString, int timeout, int maxRows, DatabaseEngine engine = DatabaseEngine.SqlServer, string? includedObjects = null, string? excludedObjects = null)
    {
        var provider = providers.GetProvider(engine);
        var (valid, error) = provider.ValidateConnectionString(connectionString);
        if (!valid) return OpResult.Fail(error ?? "The connection string is not valid.");

        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var env = await db.DatabaseEnvironments.FirstOrDefaultAsync(e => e.Name == environment);
        if (env is null) return OpResult.Fail("Environment not found.");
        environment = env.Name;
        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName)) return OpResult.Fail("Name is required.");
        if (await db.ManagedDatabases.AnyAsync(d => d.Name == trimmedName && d.Environment == environment))
            return OpResult.Fail("A database with that name and environment is already registered.");
        var cleanInc = string.IsNullOrWhiteSpace(includedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(includedObjects));
        var cleanExc = string.IsNullOrWhiteSpace(excludedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(excludedObjects, defaultToAll: false));
        if (DbAccessManager.Security.ObjectScope.HasConflict(cleanInc, cleanExc, out var conflict))
            return OpResult.Fail($"Conflict detected: '{conflict}' cannot be simultaneously included and excluded.");

        var entity = new ManagedDatabase
        {
            Name = trimmedName, Environment = environment, Engine = engine, Description = description,
            EncryptedConnectionString = crypto.Encrypt(connectionString), DefaultTimeoutSeconds = timeout, MaxRows = maxRows,
            IncludedObjects = cleanInc,
            ExcludedObjects = cleanExc
        };
        db.ManagedDatabases.Add(entity);
        var defaults = await db.EnvironmentDefaultGrants.Where(g => g.EnvironmentId == env.Id).ToListAsync();
        foreach (var grant in defaults)
            db.PermissionGrants.Add(new PermissionGrant { RoleId = grant.RoleId, DatabaseId = entity.Id, PermissionCode = grant.PermissionCode, Effect = PermissionEffect.Allow });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "Register", "Success", $"{name} ({environment}) [{engine}]", entity.Id, name);
        return OpResult.Success();
    }

    public async Task<OpResult> UpdateAsync(Actor actor, Guid id, string name, string environment, string? description, string? newConnectionString, int timeout, int maxRows, bool active, DatabaseEngine? engine = null, string? includedObjects = null, string? excludedObjects = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var env = await db.DatabaseEnvironments.FirstOrDefaultAsync(e => e.Name == environment);
        if (env is null) return OpResult.Fail("Environment not found.");
        environment = env.Name;
        var d = await db.ManagedDatabases.FindAsync(id);
        if (d is null) return OpResult.Fail("Not found.");

        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName)) return OpResult.Fail("Name is required.");
        if (await db.ManagedDatabases.AnyAsync(x => x.Id != id && x.Name == trimmedName && x.Environment == environment))
            return OpResult.Fail("A database with that name and environment is already registered.");

        d.Name = trimmedName;
        d.Environment = environment;
        if (engine.HasValue) d.Engine = engine.Value;
        d.Description = description;
        d.DefaultTimeoutSeconds = timeout;
        d.MaxRows = maxRows;
        d.IsActive = active;
        var cleanInc = string.IsNullOrWhiteSpace(includedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(includedObjects));
        var cleanExc = string.IsNullOrWhiteSpace(excludedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(excludedObjects, defaultToAll: false));
        if (DbAccessManager.Security.ObjectScope.HasConflict(cleanInc, cleanExc, out var conflict))
            return OpResult.Fail($"Conflict detected: '{conflict}' cannot be simultaneously included and excluded.");

        d.IncludedObjects = cleanInc;
        d.ExcludedObjects = cleanExc;

        if (!string.IsNullOrWhiteSpace(newConnectionString))
        {
            var provider = providers.GetProvider(d.Engine);
            var (valid, error) = provider.ValidateConnectionString(newConnectionString);
            if (!valid) return OpResult.Fail(error ?? "The connection string is not valid.");
            d.EncryptedConnectionString = crypto.Encrypt(newConnectionString);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "Update", "Success", $"{d.Name} ({d.Environment}) [{d.Engine}] active={active}", d.Id, d.Name);
        return OpResult.Success();
    }

    public async Task<List<DatabaseRoleScopeRule>> GetRoleScopeRulesAsync(Guid databaseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.DatabaseRoleScopeRules.AsNoTracking()
            .Where(r => r.DatabaseId == databaseId)
            .ToListAsync();
    }

    public async Task<OpResult> SaveRoleScopeRuleAsync(Actor actor, Guid databaseId, string roleId, string? includedObjects, string? excludedObjects)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var database = await db.ManagedDatabases.FindAsync(databaseId);
        if (database is null) return OpResult.Fail("Database not found.");
        var role = await db.Roles.FindAsync(roleId);
        if (role is null) return OpResult.Fail("Role not found.");

        var cleanInc = string.IsNullOrWhiteSpace(includedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(includedObjects));
        var cleanExc = string.IsNullOrWhiteSpace(excludedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(excludedObjects, defaultToAll: false));
        if (DbAccessManager.Security.ObjectScope.HasConflict(cleanInc, cleanExc, out var conflict))
            return OpResult.Fail($"Conflict detected: '{conflict}' cannot be simultaneously included and excluded.");

        var rule = await db.DatabaseRoleScopeRules.FirstOrDefaultAsync(r => r.DatabaseId == databaseId && r.RoleId == roleId);
        if (rule is null)
        {
            rule = new DatabaseRoleScopeRule
            {
                DatabaseId = databaseId,
                RoleId = roleId,
                IncludedObjects = cleanInc,
                ExcludedObjects = cleanExc,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.DatabaseRoleScopeRules.Add(rule);
        }
        else
        {
            rule.IncludedObjects = cleanInc;
            rule.ExcludedObjects = cleanExc;
            rule.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "RoleScopeRuleSaved", "Success", $"db={database.Name}; role={role.Name}; inc={rule.IncludedObjects}; exc={rule.ExcludedObjects}", databaseId, database.Name);
        return OpResult.Success();
    }

    public async Task<OpResult> DeleteRoleScopeRuleAsync(Actor actor, Guid databaseId, string roleId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var rule = await db.DatabaseRoleScopeRules.FirstOrDefaultAsync(r => r.DatabaseId == databaseId && r.RoleId == roleId);
        if (rule is null) return OpResult.Success();
        db.DatabaseRoleScopeRules.Remove(rule);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "RoleScopeRuleDeleted", "Success", $"db={databaseId}; role={roleId}", databaseId);
        return OpResult.Success();
    }

    public async Task<OpResult> UpdateDatabaseObjectRulesAsync(Actor actor, Guid databaseId, string? includedObjects, string? excludedObjects)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var database = await db.ManagedDatabases.FindAsync(databaseId);
        if (database is null) return OpResult.Fail("Database not found.");

        var cleanInc = string.IsNullOrWhiteSpace(includedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(includedObjects));
        var cleanExc = string.IsNullOrWhiteSpace(excludedObjects) ? null : string.Join(", ", DbAccessManager.Security.ObjectScope.ParseList(excludedObjects, defaultToAll: false));
        if (DbAccessManager.Security.ObjectScope.HasConflict(cleanInc, cleanExc, out var conflict))
            return OpResult.Fail($"Conflict detected: '{conflict}' cannot be simultaneously included and excluded.");

        database.IncludedObjects = cleanInc;
        database.ExcludedObjects = cleanExc;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "DatabaseObjectRulesUpdated", "Success", $"db={database.Name}; inc={database.IncludedObjects}; exc={database.ExcludedObjects}", databaseId, database.Name);
        return OpResult.Success();
    }
}

public sealed record DbColumnInfo(
    string Name,
    string DataType,
    bool Nullable,
    bool IsIdentity = false,
    bool HasDefault = false,
    bool IsComputed = false);
public sealed record DbForeignKeyColumn(string Column, string ReferencedColumn);
public sealed record DbForeignKeyInfo(string Name, string ReferencedSchema, string ReferencedTable, List<DbForeignKeyColumn> Columns);
public sealed record DbObjectInfo(string Schema, string Name, string Type, List<string> Columns)
{
    public bool IsTableValuedFunction { get; init; }
    public List<DbColumnInfo> ColumnDetails { get; } = [];
    public List<DbForeignKeyInfo> ForeignKeys { get; } = [];
}

public sealed record MetadataCacheEntry(Guid DatabaseId, int ObjectCount, DateTimeOffset LoadedAt);

public sealed class MetadataService(IDbContextFactory<ApplicationDbContext> factory, CryptoService crypto, IMemoryCache cache, SettingsService settings, DatabaseProviderFactory providers)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, MetadataCacheEntry> entries = new();
    private CancellationTokenSource resetToken = new();

    private static string Key(Guid id) => "meta:" + id;

    public Task<bool> IsEnabledAsync() => settings.GetBoolAsync("MetadataCacheEnabled");

    // Entries that are still alive in the cache (expired ones are ignored).
    public IReadOnlyList<MetadataCacheEntry> Status() =>
        entries.Values.Where(e => cache.TryGetValue(Key(e.DatabaseId), out _)).OrderBy(e => e.LoadedAt).ToList();

    public void Clear(Guid? databaseId = null)
    {
        if (databaseId is { } id) { cache.Remove(Key(id)); entries.TryRemove(id, out _); return; }
        var old = Interlocked.Exchange(ref resetToken, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        entries.Clear();
    }

    public async Task<List<DbObjectInfo>> GetAsync(Guid databaseId, bool refresh = false)
    {
        var enabled = await IsEnabledAsync();
        var key = Key(databaseId);
        if (enabled && !refresh && cache.TryGetValue(key, out List<DbObjectInfo>? cached) && cached is not null) return cached;

        await using var db = await factory.CreateDbContextAsync();
        var database = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId && d.IsActive)
            ?? throw new InvalidOperationException("Database not found.");

        var provider = providers.GetProvider(database.Engine);
        var connStr = crypto.Decrypt(database.EncryptedConnectionString);
        var list = await provider.LoadMetadataAsync(connStr);

        if (enabled)
        {
            var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, await settings.GetIntAsync("MetadataTtlMinutes"))) }
                .AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(resetToken.Token));
            cache.Set(key, list, options);
            entries[databaseId] = new MetadataCacheEntry(databaseId, list.Count, DateTimeOffset.UtcNow);
        }
        else entries.TryRemove(databaseId, out _);
        return list;
    }
}

public sealed class TokenService(SymmetricSecurityKey key, IDbContextFactory<ApplicationDbContext> factory)
{
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromHours(8);
    private readonly SymmetricSecurityKey _key = key;

    public static SymmetricSecurityKey BuildKey(IConfiguration config, IHostEnvironment? environment = null)
    {
        var configured = config["Jwt:Key"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new SymmetricSecurityKey(SHA512.HashData(Encoding.UTF8.GetBytes(configured)));
        }

        var keyDirectory = config["DataProtection:KeyPath"];
        var directory = !string.IsNullOrWhiteSpace(keyDirectory)
            ? keyDirectory
            : (environment is not null ? Path.Combine(environment.ContentRootPath, "App_Data") : null);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
                var keyFileName = (environment?.IsDevelopment() ?? false) ? "dev-jwt.key" : "jwt.key";
                var path = Path.Combine(directory, keyFileName);
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
                }
                var bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (bytes.Length >= 32)
                {
                    return new SymmetricSecurityKey(bytes);
                }
            }
            catch
            {
                // Fall back to ephemeral bytes if storage is not accessible
            }
        }

        return new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
    }

    public SymmetricSecurityKey Key => _key;
    public const string Issuer = "DbAccessManager";

    public string CreateAccessToken(ApplicationUser user, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.Add(AccessLifetime),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string> CreateRefreshTokenAsync(string userId)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await using var db = await factory.CreateDbContextAsync();
        db.RefreshTokens.Add(new RefreshToken { UserId = userId, TokenHash = CryptoService.Sha256(raw), ExpiresAt = DateTimeOffset.UtcNow.Add(RefreshLifetime) });
        await db.SaveChangesAsync();
        return raw;
    }

    // Rotates a refresh token; reuse of a revoked token revokes the user's whole token family.
    public async Task<(string UserId, string NewRefresh)?> RotateAsync(string raw)
    {
        var hash = CryptoService.Sha256(raw);
        await using var db = await factory.CreateDbContextAsync();
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (token is null) return null;
        if (token.RevokedAt is not null)
        {
            await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
            return null;
        }
        if (token.ExpiresAt < DateTimeOffset.UtcNow) return null;
        var next = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var nextHash = CryptoService.Sha256(next);
        token.RevokedAt = DateTimeOffset.UtcNow;
        token.ReplacedByHash = nextHash;
        db.RefreshTokens.Add(new RefreshToken { UserId = token.UserId, TokenHash = nextHash, ExpiresAt = token.ExpiresAt });
        await db.SaveChangesAsync();
        return (token.UserId, next);
    }

    public async Task RevokeAsync(string raw)
    {
        var hash = CryptoService.Sha256(raw);
        await using var db = await factory.CreateDbContextAsync();
        await db.RefreshTokens.Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
    }
}

public sealed class ClientInfoCircuitHandler(IHttpContextAccessor accessor, ClientInfo info) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var ctx = accessor.HttpContext;
        if (ctx is not null)
        {
            info.Ip = ctx.Connection.RemoteIpAddress?.ToString();
            info.UserAgent = ctx.Request.Headers.UserAgent.ToString();
        }
        return Task.CompletedTask;
    }
}

public sealed class MaintenanceWorker(IServiceScopeFactory scopes, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Maintenance tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var requests = scope.ServiceProvider.GetRequiredService<RequestService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var now = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(ct);

        var reminderWindow = now.AddHours(await settings.GetIntAsync("ExpiryReminderHours"));
        var soon = await db.PermissionGrants.Where(g => g.UserId != null && g.EndsAt != null && g.EndsAt > now && g.EndsAt <= reminderWindow && g.ReminderSentAt == null).ToListAsync(ct);
        foreach (var g in soon.GroupBy(g => g.UserId!))
        {
            await notifications.NotifyAsync(g.Key, "Access expiring soon", $"{g.Count()} permission(s) expire by {g.Min(x => x.EndsAt):u}. Request an extension if needed.", "/access");
            foreach (var grant in g) grant.ReminderSentAt = now;
        }

        var expired = await db.PermissionGrants.Where(g => g.EndsAt != null && g.EndsAt <= now.AddDays(-1)).ToListAsync(ct);
        if (expired.Count > 0)
        {
            db.PermissionGrants.RemoveRange(expired);
            await audit.TryWriteAsync(null, "System", "PurgeExpiredGrants", "Success", $"{expired.Count} grants removed");
        }
        await db.SaveChangesAsync(ct);

        var stale = await db.AccessRequests.Where(r => r.Status == RequestStatus.Pending && r.CreatedAt < now.AddDays(-14)).ToListAsync(ct);
        foreach (var r in stale) r.Status = RequestStatus.Expired;
        await db.SaveChangesAsync(ct);

        var due = await db.ScriptRequests.Where(r => r.Status == RequestStatus.Approved && r.AutoRun).Select(r => new { r.Id, r.RequesterId }).ToListAsync(ct);
        foreach (var item in due)
        {
            var actor = new Actor(item.RequesterId, "System", null, "MaintenanceWorker");
            await requests.RunApprovedScriptAsync(actor, item.Id, "System");
        }
    }
}
