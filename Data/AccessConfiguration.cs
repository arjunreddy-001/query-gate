namespace DbAccessManager.Data;

public sealed class DatabaseEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
}

public sealed class EnvironmentDefaultGrant
{
    public Guid EnvironmentId { get; set; }
    public required string RoleId { get; set; }
    public required string PermissionCode { get; set; }
}
