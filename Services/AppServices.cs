using System.Net;
using System.Net.Mail;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed class NotificationService(IDbContextFactory<ApplicationDbContext> factory, IConfiguration config, ILogger<NotificationService> logger)
{
    public async Task NotifyAsync(string userId, string title, string message, string? link = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.Notifications.Add(new Notification { UserId = userId, Title = title, Message = message, Link = link });
            var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
            await db.SaveChangesAsync();
            if (!string.IsNullOrEmpty(email)) _ = Task.Run(() => SendEmailAsync(email, title, message));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification failed for {UserId}", userId);
        }
    }

    public async Task NotifyRoleAsync(string role, string title, string message, string? link = null, string? exceptUserId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = await (from ur in db.UserRoles
                         join r in db.Roles on ur.RoleId equals r.Id
                         join u in db.Users on ur.UserId equals u.Id
                         where r.Name == role && u.IsActive && ur.UserId != exceptUserId
                         select ur.UserId).ToListAsync();
        foreach (var id in ids) await NotifyAsync(id, title, message, link);
    }

    public async Task<int> UnreadCountAsync(string userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
    }

    public async Task<List<Notification>> ListAsync(string userId, int take = 100)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Take(take).ToListAsync();
    }

    public async Task MarkReadAsync(string userId, long? id = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Notifications.Where(n => n.UserId == userId && !n.IsRead && (id == null || n.Id == id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
    }

    private async Task SendEmailAsync(string to, string subject, string body)
    {
        var host = config["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host)) return;
        try
        {
            using var client = new SmtpClient(host, config.GetValue("Smtp:Port", 587)) { EnableSsl = config.GetValue("Smtp:EnableSsl", true) };
            var user = config["Smtp:User"];
            if (!string.IsNullOrEmpty(user)) client.Credentials = new NetworkCredential(user, config["Smtp:Password"]);
            using var mail = new MailMessage(config["Smtp:From"] ?? "noreply@localhost", to, "[Query Gate] " + subject, body);
            await client.SendMailAsync(mail);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email delivery failed");
        }
    }
}

public sealed record OpResult(bool Ok, string? Error = null, string? Value = null)
{
    public static OpResult Success(string? value = null) => new(true, null, value);
    public static OpResult Fail(string error) => new(false, error);
}

public sealed class UserAdminService(
    IDbContextFactory<ApplicationDbContext> factory,
    IServiceScopeFactory scopes,
    AuditService audit,
    ExecutionService execution)
{
    public static string GeneratePassword(int length = 16)
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", symbols = "!@#$%^&*-_=+";
        var all = lower + upper + digits + symbols;
        var chars = new List<char>
        {
            lower[Random(lower.Length)], upper[Random(upper.Length)], digits[Random(digits.Length)], symbols[Random(symbols.Length)]
        };
        while (chars.Count < Math.Max(12, length)) chars.Add(all[Random(all.Length)]);
        return new string(chars.OrderBy(_ => Random(int.MaxValue)).ToArray());
    }

    private static int Random(int max) => System.Security.Cryptography.RandomNumberGenerator.GetInt32(max);

    public async Task<List<(ApplicationUser User, string[] Roles)>> ListAsync(string? search = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(u => u.Email!.Contains(search) || (u.DisplayName != null && u.DisplayName.Contains(search)));
        var users = await q.OrderBy(u => u.Email).ToListAsync();
        var roleMap = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
        return users.Select(u => (u, roleMap.Where(r => r.UserId == u.Id).Select(r => r.Name!).OrderBy(n => n).ToArray())).ToList();
    }

    public async Task<OpResult> CreateAsync(Actor actor, string email, string displayName, string role, DateTimeOffset? expiresAt)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(email) is not null) return OpResult.Fail("A user with that email already exists.");
        var password = GeneratePassword();
        var user = new ApplicationUser
        {
            UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName, IsActive = true,
            MustChangePassword = true, ExpiresAt = expiresAt, CreatedBy = actor.UserName
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) return OpResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
        await audit.TryWriteAsync(actor, "UserAdmin", "CreateUser", "Success", $"{email} role={role}");
        return OpResult.Success(password);
    }

    public async Task<OpResult> ResetPasswordAsync(Actor actor, string userId)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return OpResult.Fail("User not found.");
        var password = GeneratePassword();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded) return OpResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));
        user.MustChangePassword = true;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await audit.TryWriteAsync(actor, "UserAdmin", "ResetPassword", "Success", user.Email);
        return OpResult.Success(password);
    }

    public async Task<OpResult> SetActiveAsync(Actor actor, string userId, bool active)
    {
        if (!active && userId == actor.UserId) return OpResult.Fail("You cannot deactivate your own account.");
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return OpResult.Fail("User not found.");
        user.IsActive = active;
        await users.UpdateAsync(user);
        if (!active)
        {
            await users.UpdateSecurityStampAsync(user);
            execution.CancelAllFor(userId);
            await using var db = await factory.CreateDbContextAsync();
            await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow));
        }
        await audit.TryWriteAsync(actor, "UserAdmin", active ? "Activate" : "Deactivate", "Success", user.Email);
        return OpResult.Success();
    }

    public async Task<OpResult> SetRoleAsync(Actor actor, string userId, string role)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return OpResult.Fail("User not found.");
        if (userId == actor.UserId && role != Roles.Admin) return OpResult.Fail("You cannot remove your own Admin role.");
        var current = await users.GetRolesAsync(user);
        await users.RemoveFromRolesAsync(user, current);
        await users.AddToRoleAsync(user, role);
        await users.UpdateSecurityStampAsync(user);
        await audit.TryWriteAsync(actor, "UserAdmin", "SetRole", "Success", $"{user.Email} -> {role}");
        return OpResult.Success();
    }

    public async Task<OpResult> SetExpiryAsync(Actor actor, string userId, DateTimeOffset? expiresAt, int? timeoutOverride)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return OpResult.Fail("User not found.");
        user.ExpiresAt = expiresAt;
        user.TimeoutOverrideSeconds = timeoutOverride;
        await users.UpdateAsync(user);
        await audit.TryWriteAsync(actor, "UserAdmin", "SetExpiry", "Success", $"{user.Email} expires={expiresAt:u} timeout={timeoutOverride}");
        return OpResult.Success();
    }
}

public sealed class RequestService(
    IDbContextFactory<ApplicationDbContext> factory,
    SettingsService settings,
    NotificationService notifications,
    AuditService audit,
    ExecutionService execution,
    PermissionService permissions)
{
    public async Task<OpResult> SubmitAccessAsync(Actor actor, Guid databaseId, string[] perms, string justification, string? ticket, int hours, bool extension)
    {
        if (perms.Length == 0 || string.IsNullOrWhiteSpace(justification)) return OpResult.Fail("Select permissions and provide a justification.");
        if (perms.Any(p => !Perm.IsValid(p))) return OpResult.Fail("Unknown permission requested.");
        if (hours is < 1 or > 24 * 90) return OpResult.Fail("Duration must be between 1 hour and 90 days.");
        await using var db = await factory.CreateDbContextAsync();
        var req = new AccessRequest
        {
            RequesterId = actor.UserId, DatabaseId = databaseId, Permissions = string.Join(",", perms.Select(p => p.ToUpperInvariant()).Distinct()),
            Justification = justification.Trim(), Ticket = ticket?.Trim(), DurationHours = hours, IsExtension = extension
        };
        db.AccessRequests.Add(req);
        db.RequestStatusHistory.Add(new RequestStatusHistory { RequestType = "Access", RequestId = req.Id, Status = RequestStatus.Pending, ChangedBy = actor.UserName });
        await db.SaveChangesAsync();
        await audit.TryWriteAsync(actor, "Request", "SubmitAccess", "Success", $"{req.Permissions} {hours}h", databaseId, null, req.Id);
        await notifications.NotifyRoleAsync(Roles.Admin, "New access request", $"{actor.UserName} requested {req.Permissions}.", "/admin/approvals", actor.UserId);
        return OpResult.Success(req.Id.ToString());
    }

    public async Task<OpResult> ReviewAccessAsync(Actor actor, Guid id, bool approve, string? comment, int? hoursOverride)
    {
        await using var db = await factory.CreateDbContextAsync();
        var req = await db.AccessRequests.FindAsync(id);
        if (req is null || req.Status != RequestStatus.Pending) return OpResult.Fail("Request is not pending.");
        if (req.RequesterId == actor.UserId && !await settings.GetBoolAsync("AllowSelfApproval")) return OpResult.Fail("You cannot review your own request.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) return OpResult.Fail("A comment is required when rejecting.");
        req.Status = approve ? RequestStatus.Approved : RequestStatus.Rejected;
        req.ReviewerId = actor.UserId;
        req.ReviewedAt = DateTimeOffset.UtcNow;
        req.Comments = comment;
        if (approve)
        {
            var now = DateTimeOffset.UtcNow;
            var ends = now.AddHours(hoursOverride ?? req.DurationHours ?? 24);
            foreach (var code in req.Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                db.PermissionGrants.Add(new PermissionGrant
                {
                    UserId = req.RequesterId, DatabaseId = req.DatabaseId, PermissionCode = code, Effect = PermissionEffect.Allow,
                    StartsAt = now, EndsAt = ends, SourceRequestId = req.Id
                });
            }
        }
        db.RequestStatusHistory.Add(new RequestStatusHistory { RequestType = "Access", RequestId = id, Status = req.Status, ChangedBy = actor.UserName, Comment = comment });
        await db.SaveChangesAsync();
        await audit.TryWriteAsync(actor, "Request", approve ? "ApproveAccess" : "RejectAccess", "Success", req.Permissions, req.DatabaseId, null, id);
        await notifications.NotifyAsync(req.RequesterId, approve ? "Access approved" : "Access rejected", comment ?? (approve ? "Your access request was approved." : "Your access request was rejected."), "/access");
        return OpResult.Success();
    }

    public async Task<OpResult> CancelAsync(Actor actor, string type, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (type == "Access")
        {
            var r = await db.AccessRequests.FirstOrDefaultAsync(x => x.Id == id && x.RequesterId == actor.UserId && x.Status == RequestStatus.Pending);
            if (r is null) return OpResult.Fail("Request not found or not pending.");
            r.Status = RequestStatus.Cancelled;
        }
        else
        {
            var r = await db.ScriptRequests.FirstOrDefaultAsync(x => x.Id == id && x.RequesterId == actor.UserId && x.Status == RequestStatus.Pending);
            if (r is null) return OpResult.Fail("Request not found or not pending.");
            r.Status = RequestStatus.Cancelled;
        }
        db.RequestStatusHistory.Add(new RequestStatusHistory { RequestType = type, RequestId = id, Status = RequestStatus.Cancelled, ChangedBy = actor.UserName });
        await db.SaveChangesAsync();
        return OpResult.Success();
    }

    public async Task<OpResult> SubmitScriptAsync(Actor actor, Guid databaseId, string title, string script, string justification, SqlGate gate)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(justification))
            return OpResult.Fail("Title, script and justification are required.");
        var analysis = gate.Analyze(script);
        if (analysis.HasHardDenial) return OpResult.Fail("Script rejected by the SQL gate: " + string.Join(" ", analysis.ParseErrors.Concat(analysis.HardDenials)));
        await using var db = await factory.CreateDbContextAsync();
        var req = new ScriptRequest { RequesterId = actor.UserId, DatabaseId = databaseId, Title = title.Trim(), ScriptText = script, Justification = justification.Trim() };
        db.ScriptRequests.Add(req);
        db.RequestStatusHistory.Add(new RequestStatusHistory { RequestType = "Script", RequestId = req.Id, Status = RequestStatus.Pending, ChangedBy = actor.UserName });
        await db.SaveChangesAsync();
        await audit.TryWriteAsync(actor, "Request", "SubmitScript", "Success", title, databaseId, null, req.Id);
        await notifications.NotifyRoleAsync(Roles.Admin, "New script request", $"{actor.UserName}: {title}", "/admin/approvals", actor.UserId);
        return OpResult.Success(req.Id.ToString());
    }

    public async Task<OpResult> ReviewScriptAsync(Actor actor, Guid id, bool approve, string? comment, string? modifiedScript, bool autoRun, SqlGate gate)
    {
        await using var db = await factory.CreateDbContextAsync();
        var req = await db.ScriptRequests.FindAsync(id);
        if (req is null || req.Status != RequestStatus.Pending) return OpResult.Fail("Request is not pending.");
        if (req.RequesterId == actor.UserId && !await settings.GetBoolAsync("AllowSelfApproval")) return OpResult.Fail("You cannot review your own request.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) return OpResult.Fail("A comment is required when rejecting.");
        var modified = !string.IsNullOrWhiteSpace(modifiedScript) && modifiedScript.Trim() != req.ScriptText.Trim();
        if (approve && modified && gate.Analyze(modifiedScript!).HasHardDenial) return OpResult.Fail("The modified script is rejected by the SQL gate.");
        req.Status = approve ? RequestStatus.Approved : RequestStatus.Rejected;
        req.ReviewerId = actor.UserId;
        req.ReviewedAt = DateTimeOffset.UtcNow;
        req.Comments = comment;
        if (approve)
        {
            req.ModifiedScriptText = modified ? modifiedScript : null;
            req.AutoRun = autoRun && !modified;
        }
        db.RequestStatusHistory.Add(new RequestStatusHistory { RequestType = "Script", RequestId = id, Status = req.Status, ChangedBy = actor.UserName, Comment = comment });
        await db.SaveChangesAsync();
        await audit.TryWriteAsync(actor, "Request", approve ? "ApproveScript" : "RejectScript", "Success", req.Title, req.DatabaseId, null, id);
        await notifications.NotifyAsync(req.RequesterId, approve ? "Script approved" : "Script rejected", comment ?? req.Title, "/scripts");
        return OpResult.Success();
    }

    public async Task<ExecutionResult> RunApprovedScriptAsync(Actor actor, Guid id, string executor)
    {
        await using var db = await factory.CreateDbContextAsync();
        var req = await db.ScriptRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (req is null || req.Status != RequestStatus.Approved)
            return new ExecutionResult { Outcome = "Denied", Error = "Script is not approved." };
        // Claim the request first so it can never be executed twice.
        var claimed = await db.ScriptRequests.Where(r => r.Id == id && r.Status == RequestStatus.Approved)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Executed));
        if (claimed == 0) return new ExecutionResult { Outcome = "Denied", Error = "Script was already executed." };

        var result = await execution.ExecuteAsync(actor, new ExecutionOptions
        {
            SqlText = req.EffectiveScript, DatabaseId = req.DatabaseId, Executor = executor, LinkedRequestId = id,
            ApprovedScript = true, ConfirmUnfilteredWrites = true, TimeoutSeconds = 300
        });
        var summary = result.Ok ? $"Success. {result.RowsAffected} rows affected, {result.RowsReturned} returned." : $"{result.Outcome}: {result.Error}";
        await db.ScriptRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, result.Ok ? RequestStatus.Executed : RequestStatus.Failed)
            .SetProperty(r => r.ExecutedAt, DateTimeOffset.UtcNow)
            .SetProperty(r => r.ExecutedBy, executor == "System" ? "System" : actor.UserName)
            .SetProperty(r => r.ResultSummary, summary)
            .SetProperty(r => r.AuditId, result.AuditId == 0 ? (long?)null : result.AuditId));
        db.RequestStatusHistory.Add(new RequestStatusHistory { RequestType = "Script", RequestId = id, Status = result.Ok ? RequestStatus.Executed : RequestStatus.Failed, ChangedBy = executor, Comment = summary });
        await db.SaveChangesAsync();
        await notifications.NotifyAsync(req.RequesterId, result.Ok ? "Script executed" : "Script failed", $"{req.Title}: {summary}", "/scripts");
        return result;
    }
}

public sealed class WorkspaceService(IDbContextFactory<ApplicationDbContext> factory)
{
    public const int MaxSavedPerDatabase = 50;

    public async Task<List<SavedQuery>> SavedAsync(string userId, Guid databaseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SavedQueries.AsNoTracking().Where(q => q.UserId == userId && q.DatabaseId == databaseId).OrderBy(q => q.Name).ToListAsync();
    }

    public async Task<OpResult> SaveAsync(string userId, Guid databaseId, string name, string sql, string? tags, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql)) return OpResult.Fail("Name and SQL are required.");
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.SavedQueries.FirstOrDefaultAsync(q => q.UserId == userId && q.DatabaseId == databaseId && q.Name == name);
        if (existing is null)
        {
            if (await db.SavedQueries.CountAsync(q => q.UserId == userId && q.DatabaseId == databaseId) >= MaxSavedPerDatabase)
                return OpResult.Fail($"Limit of {MaxSavedPerDatabase} saved queries per database reached.");
            db.SavedQueries.Add(new SavedQuery { UserId = userId, DatabaseId = databaseId, Name = name.Trim(), SqlText = sql, Tags = tags, Description = description });
        }
        else
        {
            existing.SqlText = sql; existing.Tags = tags; existing.Description = description; existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        return OpResult.Success();
    }

    public async Task DeleteAsync(string userId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.SavedQueries.Where(q => q.Id == id && q.UserId == userId).ExecuteDeleteAsync();
    }

    public async Task<List<QueryHistory>> HistoryAsync(string userId, Guid databaseId, int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.QueryHistory.AsNoTracking().Where(h => h.UserId == userId && h.DatabaseId == databaseId).OrderByDescending(h => h.Id).Take(take).ToListAsync();
    }
}

