using System.Security.Cryptography;
using System.Text;
using DbAccessManager.Data;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed record Actor(string UserId, string UserName, string? Ip, string? UserAgent);

public sealed class ClientInfo
{
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class CryptoService
{
    private readonly byte[] _key;

    public CryptoService(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Encryption:Key"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = Convert.FromBase64String(configured);
            if (_key.Length != 32) throw new InvalidOperationException("Encryption:Key must be a base64-encoded 32-byte (AES-256) key.");
            return;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Encryption:Key is required outside Development. Provide a base64 32-byte key via environment variable or secret store.");
        }

        var directory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dev-encryption.key");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
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
        if (!payload.StartsWith("v1:", StringComparison.Ordinal)) throw new CryptographicException("Unknown payload format.");
        var raw = Convert.FromBase64String(payload[3..]);
        var nonce = raw[..12];
        var tag = raw[12..28];
        var cipher = raw[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class SettingsService(IDbContextFactory<ApplicationDbContext> factory)
{
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
        ["PasswordResetLength"] = ("16", "Generated temporary password length")
    };

    public async Task<string> GetAsync(string key)
    {
        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        return stored?.Value ?? Definitions[key].Default;
    }

    public async Task<int> GetIntAsync(string key) =>
        int.TryParse(await GetAsync(key), out var value) ? value : int.Parse(Definitions[key].Default);

    public async Task<bool> GetBoolAsync(string key) => bool.TryParse(await GetAsync(key), out var v) && v;

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        return Definitions.ToDictionary(d => d.Key, d => stored.GetValueOrDefault(d.Key, d.Value.Default));
    }

    public async Task SetAsync(string key, string value)
    {
        if (!Definitions.ContainsKey(key)) throw new ArgumentException("Unknown setting.", nameof(key));
        await SetRawAsync(key, value);
    }

    public async Task<string?> GetRawAsync(string key)
    {
        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        return stored?.Value;
    }

    public async Task SetRawAsync(string key, string value)
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Settings.FindAsync(key);
        if (existing is null) db.Settings.Add(new SettingEntry { Key = key, Value = value });
        else existing.Value = value;
        await db.SaveChangesAsync();
    }

    public async Task<SmtpConfig> GetSmtpConfigAsync(CryptoService crypto, IConfiguration? fallback = null)
    {
        var host = await GetRawAsync("Smtp:Host");
        var portStr = await GetRawAsync("Smtp:Port");
        var sslStr = await GetRawAsync("Smtp:EnableSsl");
        var user = await GetRawAsync("Smtp:User");
        var encPwd = await GetRawAsync("Smtp:EncryptedPassword");
        var from = await GetRawAsync("Smtp:From");

        // Fallback to configuration if host is not configured in DB
        if (string.IsNullOrWhiteSpace(host) && fallback is not null)
        {
            host = fallback["Smtp:Host"];
            portStr ??= fallback["Smtp:Port"];
            sslStr ??= fallback["Smtp:EnableSsl"];
            user ??= fallback["Smtp:User"];
            var rawFallbackPwd = fallback["Smtp:Password"];
            from ??= fallback["Smtp:From"];

            if (!string.IsNullOrWhiteSpace(host))
            {
                var port = int.TryParse(portStr, out var p) ? p : 587;
                var ssl = !bool.TryParse(sslStr, out var s) || s;
                return new SmtpConfig(host, port, ssl, user, rawFallbackPwd, from ?? "noreply@localhost", true, !string.IsNullOrEmpty(rawFallbackPwd));
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
        return new SmtpConfig(host ?? "", dbPort, dbSsl, user, decryptedPwd, from ?? "noreply@localhost", isConfigured, hasEncPwd);
    }

    public async Task SaveSmtpConfigAsync(CryptoService crypto, string host, int port, bool enableSsl, string? user, string? newPassword, string from)
    {
        await SetRawAsync("Smtp:Host", (host ?? "").Trim());
        await SetRawAsync("Smtp:Port", port.ToString());
        await SetRawAsync("Smtp:EnableSsl", enableSsl ? "true" : "false");
        await SetRawAsync("Smtp:User", (user ?? "").Trim());
        await SetRawAsync("Smtp:From", (from ?? "").Trim());
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
    bool HasSavedPassword);

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
