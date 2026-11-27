using DbAccessManager.Security;

namespace DbAccessManager.Data;

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ContactEmail { get; set; }
    public string? Plan { get; set; }
    public int? MaxDatabases { get; set; }
    public int? MaxUsers { get; set; }
}

public sealed class TenantDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Domain { get; set; }
    public bool IsVerified { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TenantRole
{
    public Guid TenantId { get; set; }
    public required string RoleId { get; set; }
}

// Allows a user to belong to multiple tenants.
public sealed class UserTenantMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Role { get; set; } = Roles.Developer;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}
