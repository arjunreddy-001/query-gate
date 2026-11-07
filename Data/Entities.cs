namespace DbAccessManager.Data;

public sealed class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? Roles { get; set; }
    public Guid? DatabaseId { get; set; }
    public string? DatabaseName { get; set; }
    public required string Category { get; set; }
    public required string Action { get; set; }
    public string? SqlText { get; set; }
    public string? StatementTypes { get; set; }
    public required string Outcome { get; set; }
    public long? RowsAffected { get; set; }
    public long? RowsReturned { get; set; }
    public long? DurationMs { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string? RequestId { get; set; }
    public Guid? LinkedRequestId { get; set; }
    public string? Executor { get; set; }
    public string? Details { get; set; }
}

public sealed class AppLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public required string Level { get; set; }
    public required string Message { get; set; }
    public string? Exception { get; set; }
    public string? CorrelationId { get; set; }
    public string? UserName { get; set; }
    public string? Endpoint { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
}

public sealed class SavedQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserId { get; set; }
    public Guid DatabaseId { get; set; }
    public required string Name { get; set; }
    public required string SqlText { get; set; }
    public string? Tags { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QueryHistory
{
    public long Id { get; set; }
    public required string UserId { get; set; }
    public Guid DatabaseId { get; set; }
    public required string SqlText { get; set; }
    public required string Outcome { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class RequestStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string Executed = "Executed";
    public const string Failed = "Failed";
}

public sealed class AccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RequesterId { get; set; }
    public Guid DatabaseId { get; set; }
    public required string Permissions { get; set; }
    public required string Justification { get; set; }
    public string? Ticket { get; set; }
    public int? DurationHours { get; set; }
    public bool IsExtension { get; set; }
    public string Status { get; set; } = RequestStatus.Pending;
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? Comments { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ScriptRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RequesterId { get; set; }
    public Guid DatabaseId { get; set; }
    public required string Title { get; set; }
    public required string ScriptText { get; set; }
    public string? ModifiedScriptText { get; set; }
    public required string Justification { get; set; }
    public string Status { get; set; } = RequestStatus.Pending;
    public bool AutoRun { get; set; }
    public string? ReviewerId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? Comments { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? ExecutedBy { get; set; }
    public string? ResultSummary { get; set; }
    public long? AuditId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string EffectiveScript => ModifiedScriptText ?? ScriptText;
}

public sealed class RequestStatusHistory
{
    public long Id { get; set; }
    public required string RequestType { get; set; }
    public Guid RequestId { get; set; }
    public required string Status { get; set; }
    public string? ChangedBy { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Notification
{
    public long Id { get; set; }
    public required string UserId { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SettingEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
