using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DbAccessManager.Services;

public sealed record Actor(string UserId, string UserName, string? Ip, string? UserAgent, Guid? TenantId = null);

public sealed class ClientInfo
{
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class CryptoService
{
    private readonly byte[] _key;
    private readonly MemoryCache _decryptionCache = new(new MemoryCacheOptions { SizeLimit = 2048 });

    public CryptoService(IConfiguration configuration, IHostEnvironment? environment = null)
    {
        var configured = configuration["Encryption:Key"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32) throw new InvalidOperationException("Encryption:Key must be a base64-encoded 32-byte (AES-256) key.");
            return;
        }

        if (environment is null)
        {
            throw new InvalidOperationException("Encryption:Key must be configured when no IHostEnvironment is provided.");
        }

        var keyDirectory = configuration["DataProtection:KeyPath"];
        var directory = !string.IsNullOrWhiteSpace(keyDirectory)
            ? keyDirectory
            : Path.Combine(environment.ContentRootPath, "App_Data");

        Directory.CreateDirectory(directory);
        var keyFileName = environment.IsDevelopment() ? "dev-encryption.key" : "encryption.key";
        var path = Path.Combine(directory, keyFileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }
        _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        if (_key.Length != 32) throw new InvalidOperationException($"Encryption key in '{path}' must be a base64-encoded 32-byte (AES-256) key.");
    }

    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Decrypt(string payload)
    {
        if (_decryptionCache.TryGetValue(payload, out string? cached) && cached is not null) return cached;
        if (!payload.StartsWith("v1:", StringComparison.Ordinal)) throw new CryptographicException("Unknown payload format.");
        var raw = Convert.FromBase64String(payload[3..]);
        var nonce = raw[..12];
        var tag = raw[12..28];
        var cipher = raw[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        var result = Encoding.UTF8.GetString(plain);
        _decryptionCache.Set(payload, result, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = TimeSpan.FromHours(1)
        });
        return result;
    }

    public void ClearCache() => _decryptionCache.Compact(1.0);

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class SettingsService(
    IDbContextFactory<ApplicationDbContext> factory,
    IMemoryCache? cache = null,
    ITenantContextAccessor? tenantAccessor = null)
{
    private Guid ResolveTenantId() => tenantAccessor?.Current?.TenantId ?? TenantContextAccessor.FallbackDefaultTenant?.TenantId ?? Guid.Empty;

    public static readonly IReadOnlyDictionary<string, (string Default, string Label)> Definitions = new Dictionary<string, (string, string)>
    {
        ["DefaultTimeoutSeconds"] = ("30", "Default query timeout (seconds)"),
        ["MaxTimeoutSeconds"] = ("300", "Maximum timeout ceiling (seconds)"),
        ["DefaultMaxRows"] = ("1000", "Default result row limit"),
        ["MaxConcurrentPerUser"] = ("3", "Concurrent queries per user"),
        ["MaxConcurrentPerDatabase"] = ("10", "Concurrent queries per database"),
        ["AllowSelfApproval"] = ("false", "Allow approvers to approve their own requests"),
        ["HistoryLimit"] = ("100", "Query history entries kept per user"),
        ["ExpiryReminderHours"] = ("24", "Hours before expiry to send reminders"),
        ["MetadataTtlMinutes"] = ("10", "Object metadata cache TTL (minutes)"),
        ["MetadataCacheEnabled"] = ("true", "Cache object metadata (object explorer and autocomplete)"),
        ["PasswordResetLength"] = ("16", "Generated temporary password length"),
        ["HealthVisibleRoles"] = ("", "Additional roles that can view the DB Health dashboard (comma-separated, e.g. Auditor,Developer)"),
        ["Ai:Enabled"] = ("false", "Enable AI Assistant"),
        ["Ai:Provider"] = ("", "AI Provider (OpenAI, Gemini, Anthropic, Ollama)"),
        ["Ai:Endpoint"] = ("", "AI Endpoint URL"),
        ["Ai:ApiKey"] = ("", "Encrypted AI API Key"),
        ["Ai:Model"] = ("", "AI Model Name"),
        ["Ai:Temperature"] = ("0.1", "AI Temperature"),
        ["Oidc:Enabled"] = ("false", "Enable OpenID Connect SSO"),
        ["Oidc:DisplayName"] = ("Enterprise SSO", "SSO Button Display Name"),
        ["Oidc:Authority"] = ("", "OIDC Authority / Issuer URL"),
        ["Oidc:ClientId"] = ("", "OIDC Client ID"),
        ["Oidc:ClientSecret"] = ("", "Encrypted OIDC Client Secret"),
        ["Oidc:Scopes"] = ("openid profile email", "OIDC Scopes")
    };

    public async Task<string> GetAsync(string key)
    {
        var raw = await GetRawAsync(key);
        return raw ?? (Definitions.TryGetValue(key, out var def) ? def.Default : "");
    }

    public async Task<int> GetIntAsync(string key)
    {
        var raw = await GetAsync(key);
        if (int.TryParse(raw, out var value)) return value;
        return Definitions.TryGetValue(key, out var def) && int.TryParse(def.Default, out var fallback) ? fallback : 0;
    }

    public async Task<bool> GetBoolAsync(string key) => bool.TryParse(await GetAsync(key), out var v) && v;

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        var tenantId = ResolveTenantId();
        var allKey = $"settings:all:{tenantId}";
        if (cache != null && cache.TryGetValue(allKey, out Dictionary<string, string>? cachedAll) && cachedAll != null)
        {
            return new Dictionary<string, string>(cachedAll);
        }

        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        var result = Definitions.ToDictionary(d => d.Key, d => stored.GetValueOrDefault(d.Key, d.Value.Default));

        if (cache != null)
        {
            cache.Set(allKey, result, TimeSpan.FromMinutes(30));
            foreach (var (k, v) in stored)
            {
                cache.Set($"setting:raw:{tenantId}:{k}", (string?)v, TimeSpan.FromMinutes(30));
            }
        }
        return new Dictionary<string, string>(result);
    }

    public async Task SetAsync(string key, string value)
    {
        if (!Definitions.ContainsKey(key)) throw new ArgumentException("Unknown setting.", nameof(key));
        await SetRawAsync(key, value);
    }

    public async Task<string?> GetRawAsync(string key)
    {
        var tenantId = ResolveTenantId();
        var cacheKey = $"setting:raw:{tenantId}:{key}";
        if (cache != null && cache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }

        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        var val = stored?.Value;

        if (cache != null)
        {
            cache.Set(cacheKey, val, TimeSpan.FromMinutes(30));
        }
        return val;
    }

    public async Task SetRawAsync(string key, string value)
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        if (existing is null) db.Settings.Add(new SettingEntry { Key = key, Value = value });
        else existing.Value = value;
        await db.SaveChangesAsync();

        var tenantId = db.CurrentTenantId ?? ResolveTenantId();
        if (cache != null)
        {
            cache.Set($"setting:raw:{tenantId}:{key}", (string?)value, TimeSpan.FromMinutes(30));
            cache.Remove($"settings:all:{tenantId}");
        }
    }

    public void InvalidateCache(string? key = null, Guid? tenantId = null)
    {
        if (cache == null) return;
        var tId = tenantId ?? ResolveTenantId();
        if (key != null)
        {
            cache.Remove($"setting:raw:{tId}:{key}");
        }
        cache.Remove($"settings:all:{tId}");
    }

    public async Task<SmtpConfig> GetSmtpConfigAsync(CryptoService crypto, IConfiguration? fallback = null)
    {
        var host = await GetRawAsync("Smtp:Host");
        var portStr = await GetRawAsync("Smtp:Port");
        var sslStr = await GetRawAsync("Smtp:EnableSsl");
        var user = await GetRawAsync("Smtp:User");
        var encPwd = await GetRawAsync("Smtp:EncryptedPassword");
        var from = await GetRawAsync("Smtp:From");
        var fromName = await GetRawAsync("Smtp:FromName");

        // Fallback to configuration if host is not configured in DB
        if (string.IsNullOrWhiteSpace(host) && fallback is not null)
        {
            host = fallback["Smtp:Host"];
            portStr ??= fallback["Smtp:Port"];
            sslStr ??= fallback["Smtp:EnableSsl"];
            user ??= fallback["Smtp:User"];
            var rawFallbackPwd = fallback["Smtp:Password"];
            from ??= fallback["Smtp:From"];
            fromName ??= fallback["Smtp:FromName"];

            if (!string.IsNullOrWhiteSpace(host))
            {
                var port = int.TryParse(portStr, out var p) ? p : 587;
                var ssl = !bool.TryParse(sslStr, out var s) || s;
                return new SmtpConfig(host, port, ssl, user, rawFallbackPwd, ResolveSmtpFrom(from, user), true, !string.IsNullOrEmpty(rawFallbackPwd), SanitizeHeaderValue(fromName));
            }
        }

        var hasEncPwd = !string.IsNullOrWhiteSpace(encPwd);
        string? decryptedPwd = null;
        if (hasEncPwd)
        {
            try { decryptedPwd = crypto.Decrypt(encPwd!); }
            catch { /* if key changed or invalid, handle gracefully */ }
        }

        var dbPort = int.TryParse(portStr, out var dp) ? dp : 587;
        var dbSsl = !bool.TryParse(sslStr, out var ds) || ds;
        var isConfigured = !string.IsNullOrWhiteSpace(host);
        return new SmtpConfig(host ?? "", dbPort, dbSsl, user, decryptedPwd, ResolveSmtpFrom(from, user), isConfigured, hasEncPwd, SanitizeHeaderValue(fromName));
    }

    internal static string ResolveSmtpFrom(string? from, string? user)
    {
        if (!string.IsNullOrWhiteSpace(from)) return from.Trim();
        if (!string.IsNullOrWhiteSpace(user) && System.Net.Mail.MailAddress.TryCreate(user.Trim(), out _)) return user.Trim();
        return "noreply@localhost";
    }

    internal static string? SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Replace("\r", "").Replace("\n", "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    public async Task SaveSmtpConfigAsync(CryptoService crypto, string host, int port, bool enableSsl, string? user, string? newPassword, string from, string? fromName = null)
    {
        var trimmedHost = (host ?? "").Trim();
        var trimmedUser = (user ?? "").Trim();
        var resolvedFrom = string.IsNullOrWhiteSpace(trimmedHost)
            ? (from ?? "").Trim()
            : ResolveSmtpFrom(from, trimmedUser);
        var cleanFromName = SanitizeHeaderValue(fromName) ?? "";

        await SetRawAsync("Smtp:Host", trimmedHost);
        await SetRawAsync("Smtp:Port", port.ToString());
        await SetRawAsync("Smtp:EnableSsl", enableSsl ? "true" : "false");
        await SetRawAsync("Smtp:User", trimmedUser);
        await SetRawAsync("Smtp:From", resolvedFrom);
        await SetRawAsync("Smtp:FromName", cleanFromName);
        if (!string.IsNullOrEmpty(newPassword))
        {
            var encrypted = crypto.Encrypt(newPassword);
            await SetRawAsync("Smtp:EncryptedPassword", encrypted);
        }
    }
}

public sealed record SmtpConfig(
    string Host,
    int Port,
    bool EnableSsl,
    string? User,
    string? Password,
    string From,
    bool IsConfigured,
    bool HasSavedPassword,
    string? FromName = null);

public sealed class AuditService(IDbContextFactory<ApplicationDbContext> factory, ILogger<AuditService> logger)
{
    public async Task<long> WriteAsync(AuditLog entry)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    public async Task TryWriteAsync(AuditLog entry)
    {
        try
        {
            await WriteAsync(entry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit record {Category}/{Action}", entry.Category, entry.Action);
        }
    }

    public Task TryWriteAsync(Actor? actor, string category, string action, string outcome, string? details = null, Guid? databaseId = null, string? databaseName = null, Guid? linkedRequestId = null) =>
        TryWriteAsync(new AuditLog
        {
            UserId = actor?.UserId,
            UserName = actor?.UserName,
            ClientIp = actor?.Ip,
            UserAgent = Truncate(actor?.UserAgent, 512),
            Category = category,
            Action = action,
            Outcome = outcome,
            Details = details,
            DatabaseId = databaseId,
            DatabaseName = databaseName,
            LinkedRequestId = linkedRequestId,
            Executor = "User"
        });

    public static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
