using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DbAccessManager.Services.Ai;

public sealed class OpenAiCompatibleAiClient(
    HttpClient httpClient,
    string endpoint,
    string apiKey,
    string model,
    double temperature = 0.1) : IAiClient
{
    public string ProviderName => "OpenAI / Compatible";

    private Uri BuildCompletionsUri()
    {
        var baseUri = endpoint.TrimEnd('/');
        if (!baseUri.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            if (!baseUri.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUri += "/v1";
            }
            baseUri += "/chat/completions";
        }
        return new Uri(baseUri);
    }

    private async Task<string> SendChatPromptAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var uri = BuildCompletionsUri();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
            temperature = Math.Clamp(temperature, 0.0, 1.0),
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI request failed (HTTP {(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return "";

        var firstChoice = choices[0];
        if (firstChoice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
        {
            return content.GetString() ?? "";
        }
        return "";
    }

    public async Task<string> GenerateSqlAsync(string prompt, string schemaContext, string dialect, CancellationToken ct = default)
    {
        var sys = $"You are a database engineer writing queries for {dialect}. Output ONLY valid SQL statements. Do not surround with markdown backticks or explanations unless asked. Follow the database schema provided.\n\nDatabase Schema:\n{schemaContext}";
        var resp = await SendChatPromptAsync(sys, prompt, ct);
        return CleanSqlOutput(resp);
    }

    public async Task<string> ExplainOrFixAsync(string failedSql, string errorMessage, string schemaContext, string dialect, CancellationToken ct = default)
    {
        var sys = $"You are an expert SQL engineer. Explain why the query was denied or failed in plain human terms, and provide a corrected, safe SQL query for {dialect}.\nFormat your response as:\nEXPLANATION: <plain explanation>\nFIXED_SQL:\n<sql query>\n\nDatabase Schema:\n{schemaContext}";
        var user = $"Query:\n{failedSql}\n\nError or Denial Message:\n{errorMessage}";
        return await SendChatPromptAsync(sys, user, ct);
    }

    public async Task<string> ExplainPlanAsync(ExecutionPlanDto plan, string dialect, CancellationToken ct = default)
    {
        var sys = $"You are a database performance tuning expert for {dialect}. Analyze the execution plan, explain performance bottlenecks (scans, high-cost operators, missing indexes), and suggest concrete optimizations in plain language.";
        var sb = new StringBuilder();
        sb.AppendLine($"Total Cost: {plan.TotalCost:F4}");
        sb.AppendLine($"Estimated Rows: {plan.EstimatedRows:N0}");
        if (plan.MissingIndexes.Count > 0)
        {
            sb.AppendLine("Missing Index Recommendations:");
            foreach (var mi in plan.MissingIndexes)
            {
                sb.AppendLine($"- Table {mi.Table}: {mi.CreateIndexStatement} (Impact: +{mi.Impact}%)");
            }
        }
        sb.AppendLine("Top Cost Operators:");
        foreach (var op in plan.Operators.Take(8))
        {
            sb.AppendLine($"- Node #{op.NodeId} {op.PhysicalOp} on {op.TargetObject ?? "operator"}: Cost {op.CostPercentage}% (Subtree {op.SubtreeCost:F4}, Est Rows {op.EstimatedRows:N0}) {(op.IsWarning ? "[WARNING: " + op.WarningMessage + "]" : "")}");
        }

        return await SendChatPromptAsync(sys, sb.ToString(), ct);
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await SendChatPromptAsync("Respond with exactly: OK", "Ping", ct);
            return (true, $"Connected successfully to {model}. Response received.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static string CleanSqlOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```sql", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[6..];
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed[3..];
        }
        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed[..^3];
        }
        return trimmed.Trim();
    }
}
