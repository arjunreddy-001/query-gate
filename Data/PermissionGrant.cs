namespace DbAccessManager.Data;

public sealed class PermissionGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    public string? RoleId { get; set; }
    public Guid DatabaseId { get; set; }
    public required string PermissionCode { get; set; }
    public string? Schema { get; set; }
    public string? ObjectName { get; set; }
    public PermissionEffect Effect { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public Guid? SourceRequestId { get; set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string TargetScope => Security.ObjectScope.Format(Schema, ObjectName);
    public bool IsObjectLevel => !string.IsNullOrEmpty(Schema) || !string.IsNullOrEmpty(ObjectName);
}

public enum PermissionEffect
{
    Allow,
    Deny
}
