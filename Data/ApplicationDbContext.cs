using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<ManagedDatabase> ManagedDatabases => Set<ManagedDatabase>();
    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppLog> AppLogs => Set<AppLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();
    public DbSet<QueryHistory> QueryHistory => Set<QueryHistory>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<ScriptRequest> ScriptRequests => Set<ScriptRequest>();
    public DbSet<RequestStatusHistory> RequestStatusHistory => Set<RequestStatusHistory>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<DatabaseEnvironment> DatabaseEnvironments => Set<DatabaseEnvironment>();
    public DbSet<EnvironmentDefaultGrant> EnvironmentDefaultGrants => Set<EnvironmentDefaultGrant>();
    public DbSet<DatabaseRoleScopeRule> DatabaseRoleScopeRules => Set<DatabaseRoleScopeRule>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<DatabaseEnvironment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });
        builder.Entity<EnvironmentDefaultGrant>(entity =>
        {
            entity.HasKey(g => new { g.EnvironmentId, g.RoleId, g.PermissionCode });
            entity.Property(g => g.RoleId).HasMaxLength(450);
            entity.Property(g => g.PermissionCode).HasMaxLength(128);
            entity.HasOne<DatabaseEnvironment>().WithMany().HasForeignKey(g => g.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IdentityRole>().WithMany().HasForeignKey(g => g.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.DisplayName).HasMaxLength(200);
            entity.Property(user => user.CreatedBy).HasMaxLength(450);
        });

        builder.Entity<ManagedDatabase>(entity =>
        {
            entity.HasKey(database => database.Id);
            entity.HasIndex(database => new { database.Name, database.Environment }).IsUnique();
            entity.Property(database => database.Name).HasMaxLength(128).IsRequired();
            entity.Property(database => database.Environment).HasMaxLength(32).IsRequired();
            entity.Property(database => database.Engine).HasConversion<string>().HasMaxLength(32).HasDefaultValue(DatabaseEngine.SqlServer);
            entity.Property(database => database.Description).HasMaxLength(512);
            entity.Property(database => database.EncryptedConnectionString).IsRequired();
            entity.Property(database => database.IncludedObjects).HasMaxLength(4000);
            entity.Property(database => database.ExcludedObjects).HasMaxLength(4000);
        });

        builder.Entity<DatabaseRoleScopeRule>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.DatabaseId, r.RoleId }).IsUnique();
            entity.Property(r => r.RoleId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.IncludedObjects).HasMaxLength(4000);
            entity.Property(r => r.ExcludedObjects).HasMaxLength(4000);
            entity.HasOne<ManagedDatabase>().WithMany().HasForeignKey(r => r.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<IdentityRole>().WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PermissionGrant>(entity =>
        {
            entity.HasKey(grant => grant.Id);
            entity.Property(grant => grant.PermissionCode).HasMaxLength(128).IsRequired();
            entity.Property(grant => grant.Schema).HasMaxLength(128);
            entity.Property(grant => grant.ObjectName).HasMaxLength(128);
            entity.Property(grant => grant.Effect).HasConversion<string>().HasMaxLength(8).IsRequired();
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(grant => grant.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<IdentityRole>().WithMany().HasForeignKey(grant => grant.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ManagedDatabase>().WithMany().HasForeignKey(grant => grant.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(grant => new { grant.UserId, grant.DatabaseId, grant.PermissionCode, grant.Schema, grant.ObjectName });
            entity.HasIndex(grant => new { grant.RoleId, grant.DatabaseId, grant.PermissionCode, grant.Schema, grant.ObjectName });
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_PermissionGrants_ExactlyOnePrincipal",
                "(CASE WHEN [UserId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [RoleId] IS NULL THEN 0 ELSE 1 END) = 1"));
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Category).HasMaxLength(32);
            entity.Property(a => a.Action).HasMaxLength(64);
            entity.Property(a => a.Outcome).HasMaxLength(32);
            entity.Property(a => a.UserName).HasMaxLength(256);
            entity.Property(a => a.UserId).HasMaxLength(450);
            entity.Property(a => a.DatabaseName).HasMaxLength(128);
            entity.Property(a => a.ClientIp).HasMaxLength(64);
            entity.Property(a => a.UserAgent).HasMaxLength(512);
            entity.Property(a => a.RequestId).HasMaxLength(64);
            entity.Property(a => a.Executor).HasMaxLength(64);
            entity.HasIndex(a => new { a.UserId, a.DatabaseId, a.Timestamp });
            entity.HasIndex(a => a.Timestamp);
        });

        builder.Entity<AppLog>(entity =>
        {
            entity.Property(l => l.Level).HasMaxLength(16);
            entity.Property(l => l.CorrelationId).HasMaxLength(64);
            entity.Property(l => l.UserName).HasMaxLength(256);
            entity.Property(l => l.Endpoint).HasMaxLength(512);
            entity.HasIndex(l => l.Timestamp);
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.Property(t => t.UserId).HasMaxLength(450);
            entity.Property(t => t.TokenHash).HasMaxLength(128);
            entity.Property(t => t.ReplacedByHash).HasMaxLength(128);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.UserId);
        });

        builder.Entity<SavedQuery>(entity =>
        {
            entity.Property(q => q.UserId).HasMaxLength(450);
            entity.Property(q => q.Name).HasMaxLength(200);
            entity.Property(q => q.Tags).HasMaxLength(400);
            entity.Property(q => q.Description).HasMaxLength(1000);
            entity.HasIndex(q => new { q.UserId, q.DatabaseId, q.Name }).IsUnique();
        });

        builder.Entity<QueryHistory>(entity =>
        {
            entity.Property(q => q.UserId).HasMaxLength(450);
            entity.Property(q => q.Outcome).HasMaxLength(32);
            entity.HasIndex(q => new { q.UserId, q.ExecutedAt });
        });

        builder.Entity<AccessRequest>(entity =>
        {
            entity.Property(r => r.RequesterId).HasMaxLength(450);
            entity.Property(r => r.ReviewerId).HasMaxLength(450);
            entity.Property(r => r.Permissions).HasMaxLength(1000);
            entity.Property(r => r.Schema).HasMaxLength(128);
            entity.Property(r => r.ObjectName).HasMaxLength(128);
            entity.Property(r => r.Justification).HasMaxLength(2000);
            entity.Property(r => r.Ticket).HasMaxLength(100);
            entity.Property(r => r.Status).HasMaxLength(16);
            entity.HasIndex(r => new { r.Status, r.CreatedAt });
        });

        builder.Entity<ScriptRequest>(entity =>
        {
            entity.Property(r => r.RequesterId).HasMaxLength(450);
            entity.Property(r => r.ReviewerId).HasMaxLength(450);
            entity.Property(r => r.Title).HasMaxLength(200);
            entity.Property(r => r.Justification).HasMaxLength(2000);
            entity.Property(r => r.Status).HasMaxLength(16);
            entity.Property(r => r.ExecutedBy).HasMaxLength(64);
            entity.Ignore(r => r.EffectiveScript);
            entity.HasIndex(r => new { r.Status, r.CreatedAt });
        });

        builder.Entity<RequestStatusHistory>(entity =>
        {
            entity.Property(h => h.RequestType).HasMaxLength(16);
            entity.Property(h => h.Status).HasMaxLength(16);
            entity.Property(h => h.ChangedBy).HasMaxLength(256);
            entity.HasIndex(h => new { h.RequestType, h.RequestId });
        });

        builder.Entity<Notification>(entity =>
        {
            entity.Property(n => n.UserId).HasMaxLength(450);
            entity.Property(n => n.Title).HasMaxLength(200);
            entity.Property(n => n.Message).HasMaxLength(2000);
            entity.Property(n => n.Link).HasMaxLength(300);
            entity.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
        });

        builder.Entity<SettingEntry>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(100);
            entity.Property(s => s.Value).HasMaxLength(2000);
        });
    }
}
