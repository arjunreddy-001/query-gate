using System.Text;
using System.Text.Json;

namespace DbAccessManager.Services.Ai;

public sealed class GeminiAiClient(
    HttpClient httpClient,
    string apiKey,
    string model = "gemini-1.5-flash",
    double temperature = 0.1) : IAiClient
{
    public string ProviderName => "Google Gemini";

    private Uri BuildGeminiUri()
    {
        var m = string.IsNullOrWhiteSpace(model) ? "gemini-1.5-flash" : model;
        return new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{m}:generateContent?key={Uri.EscapeDataString(apiKey)}");
    }

    private async Task<string> GenerateContentAsync(string systemInstruction, string userPrompt, CancellationToken ct)
    {
        var uri = BuildGeminiUri();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);

        var payload = new
        {
            systemInstruction = new
            {
                parts = new object[] { new { text = systemInstruction } }
            },
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new object[] { new { text = userPrompt } }
                }
            },
            generationConfig = new
            {
                temperature = Math.Clamp(temperature, 0.0, 1.0)
            }
        };

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini request failed (HTTP {(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var cand = candidates[0];
            if (cand.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
            {
                return parts[0].GetProperty("text").GetString() ?? "";
            }
        }
        return "";
    }

    public async Task<string> GenerateSqlAsync(string prompt, string schemaContext, string dialect, CancellationToken ct = default)
    {
        var sys = $"You are a database engineer writing queries for {dialect}. Output ONLY valid SQL statements. Do not surround with markdown backticks or explanations unless asked. Follow the database schema provided.\n\nDatabase Schema:\n{schemaContext}";
        var resp = await GenerateContentAsync(sys, prompt, ct);
        return OpenAiCompatibleAiClient.CleanSqlOutput(resp);
    }

    public async Task<string> ExplainOrFixAsync(string failedSql, string errorMessage, string schemaContext, string dialect, CancellationToken ct = default)
    {
        var sys = $"You are an expert SQL engineer. Explain why the query was denied or failed in plain human terms, and provide a corrected, safe SQL query for {dialect}.\nFormat your response as:\nEXPLANATION: <plain explanation>\nFIXED_SQL:\n<sql query>\n\nDatabase Schema:\n{schemaContext}";
        var user = $"Query:\n{failedSql}\n\nError or Denial Message:\n{errorMessage}";
        return await GenerateContentAsync(sys, user, ct);
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

        return await GenerateContentAsync(sys, sb.ToString(), ct);
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await GenerateContentAsync("Respond with exactly: OK", "Ping", ct);
            return (true, $"Connected successfully to Gemini model {model}. Response received.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
