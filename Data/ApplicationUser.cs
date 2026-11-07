using Microsoft.AspNetCore.Identity;

namespace DbAccessManager.Data;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? TimeoutOverrideSeconds { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool CanSignIn(DateTimeOffset now) => IsActive && (!ExpiresAt.HasValue || ExpiresAt.Value > now);
}
