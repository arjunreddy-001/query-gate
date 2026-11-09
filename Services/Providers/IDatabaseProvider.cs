using System.Data.Common;
using DbAccessManager.Data;

namespace DbAccessManager.Services.Providers;

public interface IDatabaseProvider
{
    DatabaseEngine Engine { get; }
    string DisplayName { get; }
    DbConnection CreateConnection(string connectionString);
    (bool Valid, string? Error) ValidateConnectionString(string connectionString);
    Task<(bool Ok, string Message)> TestAsync(string connectionString);
    Task<List<DbObjectInfo>> LoadMetadataAsync(string connectionString, CancellationToken ct = default);
    Task<ExecutionPlanDto?> ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct = default);
    (string Outcome, string ErrorMessage, int? ErrorLine) TranslateException(Exception ex, int timeoutSeconds);
    void RegisterMessageListener(DbConnection connection, Action<string> onMessage);
}
