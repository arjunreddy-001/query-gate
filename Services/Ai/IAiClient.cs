namespace DbAccessManager.Services.Ai;

public interface IAiClient
{
    string ProviderName { get; }
    Task<string> GenerateSqlAsync(string prompt, string schemaContext, string dialect, CancellationToken ct = default);
    Task<string> ExplainOrFixAsync(string failedSql, string errorMessage, string schemaContext, string dialect, CancellationToken ct = default);
    Task<string> ExplainPlanAsync(ExecutionPlanDto plan, string dialect, CancellationToken ct = default);
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
}
