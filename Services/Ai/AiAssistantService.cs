using System.Text;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.Extensions.Logging;

namespace DbAccessManager.Services.Ai;

public sealed record AiSqlResult(
    bool Success,
    string? Sql,
    string? ErrorMessage,
    List<string> SafetyWarnings,
    bool IsGatePermitted);

public sealed record AiFixResult(
    bool Success,
    string? Explanation,
    string? FixedSql,
    string? ErrorMessage,
    List<string> SafetyWarnings);

public sealed class AiAssistantService(
    IHttpClientFactory httpClientFactory,
    SettingsService settings,
    CryptoService crypto,
    MetadataService metadata,
    SqlGate sqlGate,
    AuditService audit,
    ILogger<AiAssistantService> logger)
{
    public const string SettingsKeyEnabled = "Ai:Enabled";
    public const string SettingsKeyProvider = "Ai:Provider";
    public const string SettingsKeyEndpoint = "Ai:Endpoint";
    public const string SettingsKeyApiKey = "Ai:ApiKey";
    public const string SettingsKeyModel = "Ai:Model";
    public const string SettingsKeyTemperature = "Ai:Temperature";

    public async Task<bool> IsConfiguredAsync()
    {
        var enabled = await settings.GetBoolAsync(SettingsKeyEnabled);
        if (!enabled) return false;

        var provider = await settings.GetAsync(SettingsKeyProvider);
        if (string.IsNullOrWhiteSpace(provider)) return false;

        var apiKey = await GetDecryptedApiKeyAsync();
        var endpoint = await settings.GetAsync(SettingsKeyEndpoint);

        // Ollama or local endpoints may not require an API key, but need an endpoint
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(endpoint);
        }

        return !string.IsNullOrWhiteSpace(apiKey) || !string.IsNullOrWhiteSpace(endpoint);
    }

    public async Task<string> GetDecryptedApiKeyAsync()
    {
        var enc = await settings.GetAsync(SettingsKeyApiKey);
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

    public async Task SaveAiConfigAsync(bool enabled, string provider, string endpoint, string rawApiKey, string model, double temperature)
    {
        await settings.SetAsync(SettingsKeyEnabled, enabled ? "true" : "false");
        await settings.SetAsync(SettingsKeyProvider, provider ?? "");
        await settings.SetAsync(SettingsKeyEndpoint, endpoint ?? "");
        if (!string.IsNullOrWhiteSpace(rawApiKey))
        {
            var enc = crypto.Encrypt(rawApiKey);
            await settings.SetAsync(SettingsKeyApiKey, enc);
        }
        await settings.SetAsync(SettingsKeyModel, model ?? "");
        await settings.SetAsync(SettingsKeyTemperature, temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<IAiClient?> GetClientAsync(CancellationToken ct = default)
    {
        var enabled = await settings.GetBoolAsync(SettingsKeyEnabled);
        if (!enabled) return null;

        var provider = await settings.GetAsync(SettingsKeyProvider);
        var endpoint = await settings.GetAsync(SettingsKeyEndpoint);
        var apiKey = await GetDecryptedApiKeyAsync();
        var model = await settings.GetAsync(SettingsKeyModel);
        var tempStr = await settings.GetAsync(SettingsKeyTemperature);
        var temp = double.TryParse(tempStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.1;

        var http = httpClientFactory.CreateClient("AiAssistant");

        return provider.Trim().ToLowerInvariant() switch
        {
            "gemini" or "google gemini" => new GeminiAiClient(http, apiKey, model, temp),
            "anthropic" or "claude" or "anthropic claude" => new AnthropicAiClient(http, apiKey, model, temp),
            "ollama" => new OpenAiCompatibleAiClient(http, string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1" : endpoint, "", string.IsNullOrWhiteSpace(model) ? "llama3.2" : model, temp),
            _ => new OpenAiCompatibleAiClient(http, string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint, apiKey, model, temp)
        };
    }

    public async Task<string> BuildCompactSchemaContextAsync(Guid databaseId, CancellationToken ct = default)
    {
        var objects = await metadata.GetAsync(databaseId);
        var tables = objects.Where(o => o.Type is "TABLE" or "BASE TABLE" or "Table" or null).Take(80).ToList();

        var sb = new StringBuilder();
        foreach (var t in tables)
        {
            var fullName = string.IsNullOrWhiteSpace(t.Schema) ? t.Name : $"{t.Schema}.{t.Name}";
            sb.Append("TABLE ").Append(fullName).Append(" (");
            if (t.ColumnDetails.Count > 0)
            {
                sb.Append(string.Join(", ", t.ColumnDetails.Select(c => $"{c.Name} {c.DataType}{(c.IsIdentity ? " IDENTITY" : "")}")));
            }
            else
            {
                sb.Append(string.Join(", ", t.Columns));
            }
            sb.AppendLine(")");

            foreach (var fk in t.ForeignKeys)
            {
                var target = string.IsNullOrWhiteSpace(fk.ReferencedSchema) ? fk.ReferencedTable : $"{fk.ReferencedSchema}.{fk.ReferencedTable}";
                foreach (var c in fk.Columns)
                {
                    sb.AppendLine($"  FK: {fullName}.{c.Column} -> {target}.{c.ReferencedColumn}");
                }
            }
        }
        return sb.ToString();
    }

    public async Task<AiSqlResult> GenerateSqlAsync(Guid databaseId, DatabaseEngine engine, string prompt, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        if (client == null)
        {
            return new AiSqlResult(false, null, "AI Assistant is not configured. An administrator must configure your chosen AI provider in Settings -> AI Assistant.", [], false);
        }

        try
        {
            var schema = await BuildCompactSchemaContextAsync(databaseId, ct);
            var dialect = engine.ToString();
            var sql = await client.GenerateSqlAsync(prompt, schema, dialect, ct);

            if (string.IsNullOrWhiteSpace(sql))
            {
                return new AiSqlResult(false, null, "The AI provider did not return any SQL.", [], false);
            }

            // Policy Guardrail Pre-Validation
            var warnings = new List<string>();
            var gateAnalysis = sqlGate.Analyze(sql, engine);
            if (!gateAnalysis.Parsed || gateAnalysis.HasHardDenial)
            {
                foreach (var err in gateAnalysis.ParseErrors.Concat(gateAnalysis.HardDenials))
                {
                    warnings.Add($"Gate Warning: {err}");
                }
            }

            if (gateAnalysis.UnfilteredWrites.Count > 0)
            {
                warnings.Add("Safeguard Warning: Generated statement performs an UPDATE or DELETE without an explicit WHERE clause.");
            }

            if (gateAnalysis.StatementTypes.Any(t => t.Contains("DROP", StringComparison.OrdinalIgnoreCase)
                                                   || t.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase)
                                                   || t.Contains("ALTER", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("High-Risk Warning: Generated statement contains destructive DDL (DROP, TRUNCATE, or ALTER).");
            }

            await audit.TryWriteAsync(new Actor("system", "AI Assistant", null, null), "Ai", "GenerateSql", "Success", $"Prompt: {string.Concat(prompt.Take(120))}");

            return new AiSqlResult(true, sql, null, warnings, gateAnalysis.Parsed && !gateAnalysis.HasHardDenial);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI query generation failed");
            return new AiSqlResult(false, null, $"AI generation error: {ex.Message}", [], false);
        }
    }

    public async Task<AiFixResult> ExplainOrFixAsync(Guid databaseId, DatabaseEngine engine, string failedSql, string errorMessage, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        if (client == null)
        {
            return new AiFixResult(false, null, null, "AI Assistant is not configured. An administrator must configure your chosen AI provider in Settings -> AI Assistant.", []);
        }

        try
        {
            var schema = await BuildCompactSchemaContextAsync(databaseId, ct);
            var dialect = engine.ToString();
            var response = await client.ExplainOrFixAsync(failedSql, errorMessage, schema, dialect, ct);

            string explanation = response;
            string? fixedSql = null;

            var fixIdx = response.IndexOf("FIXED_SQL:", StringComparison.OrdinalIgnoreCase);
            if (fixIdx >= 0)
            {
                explanation = response[..fixIdx].Replace("EXPLANATION:", "").Trim();
                fixedSql = OpenAiCompatibleAiClient.CleanSqlOutput(response[(fixIdx + 10)..]);
            }

            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(fixedSql))
            {
                var gateAnalysis = sqlGate.Analyze(fixedSql, engine);
                if (!gateAnalysis.Parsed || gateAnalysis.HasHardDenial)
                {
                    foreach (var err in gateAnalysis.ParseErrors.Concat(gateAnalysis.HardDenials))
                    {
                        warnings.Add($"Gate Warning on Proposed Fix: {err}");
                    }
                }
            }

            return new AiFixResult(true, explanation, fixedSql, null, warnings);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI query diagnosis failed");
            return new AiFixResult(false, null, null, $"AI diagnosis error: {ex.Message}", []);
        }
    }

    public async Task<string> ExplainPlanAsync(ExecutionPlanDto plan, DatabaseEngine engine, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        if (client == null)
        {
            return "AI Assistant is not configured. Configure an AI provider in Settings -> AI Assistant to enable query plan explanations.";
        }

        try
        {
            return await client.ExplainPlanAsync(plan, engine.ToString(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI plan explanation failed");
            return $"Failed to generate AI plan explanation: {ex.Message}";
        }
    }

    public async Task<(bool Ok, string Message)> TestConfiguredConnectionAsync(string provider, string endpoint, string rawApiKey, string model, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient("AiAssistantTest");
        try
        {
            IAiClient testClient = provider.Trim().ToLowerInvariant() switch
            {
                "gemini" or "google gemini" => new GeminiAiClient(http, rawApiKey, model),
                "anthropic" or "claude" or "anthropic claude" => new AnthropicAiClient(http, rawApiKey, model),
                "ollama" => new OpenAiCompatibleAiClient(http, string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1" : endpoint, "", string.IsNullOrWhiteSpace(model) ? "llama3.2" : model),
                _ => new OpenAiCompatibleAiClient(http, string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint, rawApiKey, model)
            };

            return await testClient.TestConnectionAsync(ct);
        }
        catch (Exception ex)
        {
            return (false, $"Connection test failed: {ex.Message}");
        }
    }
}
