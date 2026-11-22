using System.Text.Json;

namespace DbAccessManager.Services;

public sealed class OidcService(
    IHttpClientFactory httpClientFactory,
    SettingsService settings,
    CryptoService crypto)
{
    public const string SettingsKeyEnabled = "Oidc:Enabled";
    public const string SettingsKeyDisplayName = "Oidc:DisplayName";
    public const string SettingsKeyAuthority = "Oidc:Authority";
    public const string SettingsKeyClientId = "Oidc:ClientId";
    public const string SettingsKeyClientSecret = "Oidc:ClientSecret";
    public const string SettingsKeyScopes = "Oidc:Scopes";

    public async Task<bool> IsEnabledAsync()
    {
        return await settings.GetBoolAsync(SettingsKeyEnabled);
    }

    public async Task<string> GetDecryptedClientSecretAsync()
    {
        var enc = await settings.GetAsync(SettingsKeyClientSecret);
        if (string.IsNullOrWhiteSpace(enc)) return "";
        try
        {
            return crypto.Decrypt(enc);
        }
        catch
        {
            return enc;
        }
    }

    public async Task SaveOidcConfigAsync(bool enabled, string displayName, string authority, string clientId, string rawClientSecret, string scopes)
    {
        await settings.SetAsync(SettingsKeyEnabled, enabled ? "true" : "false");
        await settings.SetAsync(SettingsKeyDisplayName, string.IsNullOrWhiteSpace(displayName) ? "Enterprise SSO" : displayName);
        await settings.SetAsync(SettingsKeyAuthority, authority ?? "");
        await settings.SetAsync(SettingsKeyClientId, clientId ?? "");
        if (!string.IsNullOrWhiteSpace(rawClientSecret))
        {
            var enc = crypto.Encrypt(rawClientSecret);
            await settings.SetAsync(SettingsKeyClientSecret, enc);
        }
        await settings.SetAsync(SettingsKeyScopes, string.IsNullOrWhiteSpace(scopes) ? "openid profile email" : scopes);
    }

    public async Task<(bool Ok, string Message)> TestDiscoveryAsync(string authority, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return (false, "Authority / Issuer URL cannot be empty.");
        }

        var baseUri = authority.TrimEnd('/');
        var discoveryUrl = $"{baseUri}/.well-known/openid-configuration";

        try
        {
            var http = httpClientFactory.CreateClient("OidcDiscovery");
            http.Timeout = TimeSpan.FromSeconds(10);
            using var response = await http.GetAsync(discoveryUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"OIDC discovery endpoint returned HTTP {(int)response.StatusCode}. URL: {discoveryUrl}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var issuer = root.TryGetProperty("issuer", out var iss) ? iss.GetString() : null;
            var authEndpoint = root.TryGetProperty("authorization_endpoint", out var auth) ? auth.GetString() : null;
            var tokenEndpoint = root.TryGetProperty("token_endpoint", out var tok) ? tok.GetString() : null;

            if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(authEndpoint))
            {
                return (false, "OIDC discovery document is missing required issuer or authorization_endpoint properties.");
            }

            return (true, $"Valid OIDC discovery document found for issuer: {issuer}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to reach OIDC discovery endpoint: {ex.Message}");
        }
    }
}
