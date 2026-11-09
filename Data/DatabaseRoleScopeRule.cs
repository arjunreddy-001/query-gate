namespace DbAccessManager.Data;

public sealed class DatabaseRoleScopeRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DatabaseId { get; set; }
    public required string RoleId { get; set; }
    public string? IncludedObjects { get; set; }
    public string? ExcludedObjects { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
