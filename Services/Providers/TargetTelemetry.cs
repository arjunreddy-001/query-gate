namespace DbAccessManager.Services.Providers;

public sealed record TargetTelemetry(
    int? ActiveConnections = null,
    int? MaxConnections = null,
    string? DatabaseSize = null,
    double? CacheHitRatio = null,
    int? BlockedQueriesCount = null,
    int? InFlightQueries = null,
    int? AuditTotal24h = null,
    double? AuditErrorRate24h = null,
    long? AuditP95Ms24h = null);
