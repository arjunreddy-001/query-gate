using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace DbAccessManager.Services;

public sealed class DatabaseAdminService(IDbContextFactory<ApplicationDbContext> factory, CryptoService crypto, AuditService audit)
{
    private static readonly Dictionary<(string Env, string Role), string[]> DefaultGrants = new()
    {
        [("Development", Roles.Developer)] = [Perm.Select, Perm.Insert, Perm.Update, Perm.Delete, Perm.Execute, Perm.SpHelpText],
        [("QA", Roles.Developer)] = [Perm.Select],
        [("QA", Roles.Tester)] = [Perm.Select, Perm.SpHelpText]
    };

    public static readonly string[] Environments = ["Development", "QA", "Production"];

    public async Task<(bool Ok, string Message)> TestAsync(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 10 };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return (true, $"Connected to {conn.DataSource} / {conn.Database} (SQL Server {conn.ServerVersion}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> TestRegisteredAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var database = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
        if (database is null) return (false, "Database not found.");
        return await TestAsync(crypto.Decrypt(database.EncryptedConnectionString));
    }

    public async Task<OpResult> RegisterAsync(Actor actor, string name, string environment, string? description, string connectionString, int timeout, int maxRows)
    {
        if (!Environments.Contains(environment)) return OpResult.Fail("Environment must be Development or QA.");
        try { _ = new SqlConnectionStringBuilder(connectionString); }
        catch (Exception) { return OpResult.Fail("The connection string is not valid."); }
        await using var db = await factory.CreateDbContextAsync();
        var trimmedName = name.Trim();
        if (await db.ManagedDatabases.AnyAsync(d => d.Name == trimmedName && d.Environment == environment))
            return OpResult.Fail("A database with that name and environment is already registered.");
        var entity = new ManagedDatabase
        {
            Name = trimmedName, Environment = environment, Description = description,
            EncryptedConnectionString = crypto.Encrypt(connectionString), DefaultTimeoutSeconds = timeout, MaxRows = maxRows
        };
        db.ManagedDatabases.Add(entity);
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
        foreach (var ((env, role), codes) in DefaultGrants.Where(g => g.Key.Env == environment))
        {
            if (!roles.TryGetValue(role, out var roleId)) continue;
            foreach (var code in codes)
                db.PermissionGrants.Add(new PermissionGrant { RoleId = roleId, DatabaseId = entity.Id, PermissionCode = code, Effect = PermissionEffect.Allow });
        }
        await db.SaveChangesAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "Register", "Success", $"{name} ({environment})", entity.Id, name);
        return OpResult.Success();
    }

    public async Task<OpResult> UpdateAsync(Actor actor, Guid id, string name, string environment, string? description, string? newConnectionString, int timeout, int maxRows, bool active)
    {
        if (!Environments.Contains(environment)) return OpResult.Fail("Environment must be Development, QA or Production.");
        await using var db = await factory.CreateDbContextAsync();
        var d = await db.ManagedDatabases.FindAsync(id);
        if (d is null) return OpResult.Fail("Not found.");

        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName)) return OpResult.Fail("Name is required.");
        if (await db.ManagedDatabases.AnyAsync(x => x.Id != id && x.Name == trimmedName && x.Environment == environment))
            return OpResult.Fail("A database with that name and environment is already registered.");

        d.Name = trimmedName;
        d.Environment = environment;
        d.Description = description;
        d.DefaultTimeoutSeconds = timeout;
        d.MaxRows = maxRows;
        d.IsActive = active;
        if (!string.IsNullOrWhiteSpace(newConnectionString))
        {
            try { _ = new SqlConnectionStringBuilder(newConnectionString); }
            catch (Exception) { return OpResult.Fail("The connection string is not valid."); }
            d.EncryptedConnectionString = crypto.Encrypt(newConnectionString);
        }
        await db.SaveChangesAsync();
        await audit.TryWriteAsync(actor, "DatabaseAdmin", "Update", "Success", $"{d.Name} ({d.Environment}) active={active}", d.Id, d.Name);
        return OpResult.Success();
    }
}

public sealed record DbColumnInfo(string Name, string DataType, bool Nullable);
public sealed record DbForeignKeyColumn(string Column, string ReferencedColumn);
public sealed record DbForeignKeyInfo(string Name, string ReferencedSchema, string ReferencedTable, List<DbForeignKeyColumn> Columns);
public sealed record DbObjectInfo(string Schema, string Name, string Type, List<string> Columns)
{
    public bool IsTableValuedFunction { get; init; }
    public List<DbColumnInfo> ColumnDetails { get; } = [];
    public List<DbForeignKeyInfo> ForeignKeys { get; } = [];
}

public sealed record MetadataCacheEntry(Guid DatabaseId, int ObjectCount, DateTimeOffset LoadedAt);

public sealed class MetadataService(IDbContextFactory<ApplicationDbContext> factory, CryptoService crypto, IMemoryCache cache, SettingsService settings)
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
        await using var conn = new SqlConnection(crypto.Decrypt(database.EncryptedConnectionString));
        await conn.OpenAsync();
        var objects = new Dictionary<(string, string), DbObjectInfo>();

        await using (var cmd = new SqlCommand(@"SELECT s.name, o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','FN','IF','TF') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name", conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var sqlType = r.GetString(2).Trim();
                var type = sqlType switch { "U" => "Table", "V" => "View", "P" => "Procedure", _ => "Function" };
                objects[(r.GetString(0), r.GetString(1))] = new DbObjectInfo(r.GetString(0), r.GetString(1), type, [])
                {
                    IsTableValuedFunction = sqlType is "IF" or "TF"
                };
            }
        }
        await using (var cmd = new SqlCommand(@"SELECT ps.name, po.name, fk.name, rs.name, ro.name, pc.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.objects po ON po.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = po.schema_id
            JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY ps.name, po.name, fk.name, fkc.constraint_column_id", conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
            if (!objects.TryGetValue((r.GetString(0), r.GetString(1)), out var source)
                || !objects.ContainsKey((r.GetString(3), r.GetString(4)))) continue;
            var foreignKey = source.ForeignKeys.FirstOrDefault(f => f.Name == r.GetString(2));
            if (foreignKey is null)
            {
                foreignKey = new DbForeignKeyInfo(r.GetString(2), r.GetString(3), r.GetString(4), []);
                source.ForeignKeys.Add(foreignKey);
            }
            foreignKey.Columns.Add(new DbForeignKeyColumn(r.GetString(5), r.GetString(6)));
            }
        }
        await using (var cmd = new SqlCommand(@"SELECT s.name, o.name, c.name, t.name, c.is_nullable FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U','V','IF','TF') AND o.is_ms_shipped = 0 ORDER BY c.column_id", conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                if (objects.TryGetValue((r.GetString(0), r.GetString(1)), out var o))
                {
                    var column = r.GetString(2);
                    o.Columns.Add(column);
                    o.ColumnDetails.Add(new DbColumnInfo(column, r.GetString(3), r.GetBoolean(4)));
                }
        }

        var list = objects.Values.ToList();
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

    public static SymmetricSecurityKey BuildKey(IConfiguration config)
    {
        var configured = config["Jwt:Key"];
        var bytes = string.IsNullOrWhiteSpace(configured) ? RandomNumberGenerator.GetBytes(64) : SHA512.HashData(Encoding.UTF8.GetBytes(configured));
        return new SymmetricSecurityKey(bytes);
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
