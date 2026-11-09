namespace DbAccessManager.Data;

public sealed class ManagedDatabase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Environment { get; set; }
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;
    public string? Description { get; set; }
    public required string EncryptedConnectionString { get; set; }
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int MaxRows { get; set; } = 1000;
    public bool IsActive { get; set; } = true;
    public string? IncludedObjects { get; set; }
    public string? ExcludedObjects { get; set; }
}
