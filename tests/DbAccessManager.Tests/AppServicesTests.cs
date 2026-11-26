using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class AppServicesTests
{
    private static readonly Actor AdminActor = new("admin-user-id", "admin@example.com", "127.0.0.1", "TestClient");
    private static readonly Actor RegularActor = new("regular-user-id", "regular@example.com", "127.0.0.1", "TestClient");

    private sealed class TestContext : IAsyncDisposable
    {
        public SqliteConnection Connection { get; }
        public IDbContextFactory<ApplicationDbContext> Factory { get; }
        public SettingsService Settings { get; }
        public CryptoService Crypto { get; }
        public AuditService Audit { get; }
        public WebhookService Webhooks { get; }
        public NotificationService Notifications { get; }
        public RequestService Requests { get; }
        public WorkspaceService Workspace { get; }
        public SqlGate Gate { get; }

        public TestContext()
        {
            Connection = new SqliteConnection("Data Source=:memory:");
            Connection.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Connection)
                .Options;

            using (var initialDb = new ApplicationDbContext(options))
            {
                initialDb.Database.EnsureCreated();
            }

            Factory = new SimpleDbContextFactory(options);
            var inMemorySettings = new Dictionary<string, string?>
            {
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
            Crypto = new CryptoService(config);
            var memCache = new MemoryCache(new MemoryCacheOptions());
            var tenantAccessor = new TenantContextAccessor();
            Settings = new SettingsService(Factory, memCache, tenantAccessor);
            Audit = new AuditService(Factory, NullLogger<AuditService>.Instance);
            var services = new ServiceCollection();
            services.AddHttpClient();
            var serviceProvider = services.BuildServiceProvider();
            var clientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            Webhooks = new WebhookService(Factory, clientFactory, NullLogger<WebhookService>.Instance);
            Notifications = new NotificationService(Factory, config, Settings, Crypto, Webhooks, NullLogger<NotificationService>.Instance);
            Gate = new SqlGate();
            Workspace = new WorkspaceService(Factory);
            Requests = new RequestService(Factory, Settings, Notifications, Audit, null!, null);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    // --- WorkspaceService Tests ---

    [Fact]
    public async Task WorkspaceService_SaveAsync_FailsOnEmptyNameOrSql()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        var res1 = await ctx.Workspace.SaveAsync("user1", dbId, "", "SELECT 1", null, null);
        Assert.False(res1.Ok);
        Assert.Equal("Name and SQL are required.", res1.Error);

        var res2 = await ctx.Workspace.SaveAsync("user1", dbId, "My Query", "", null, null);
        Assert.False(res2.Ok);
        Assert.Equal("Name and SQL are required.", res2.Error);
    }

    [Fact]
    public async Task WorkspaceService_SaveAsync_SavesNewAndUpdatesExisting()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();
        const string userId = "user1";

        // Create new
        var saveResult = await ctx.Workspace.SaveAsync(userId, dbId, "Get Customers", "SELECT * FROM Customers", "reporting", "Active customers");
        Assert.True(saveResult.Ok);

        var list = await ctx.Workspace.SavedAsync(userId, dbId);
        Assert.Single(list);
        Assert.Equal("Get Customers", list[0].Name);
        Assert.Equal("SELECT * FROM Customers", list[0].SqlText);
        Assert.Equal("reporting", list[0].Tags);

        // Update existing with same name and database
        var updateResult = await ctx.Workspace.SaveAsync(userId, dbId, "Get Customers", "SELECT Id, Name FROM Customers", "crm", "Updated description");
        Assert.True(updateResult.Ok);

        var updatedList = await ctx.Workspace.SavedAsync(userId, dbId);
        Assert.Single(updatedList);
        Assert.Equal("SELECT Id, Name FROM Customers", updatedList[0].SqlText);
        Assert.Equal("crm", updatedList[0].Tags);
        Assert.Equal("Updated description", updatedList[0].Description);
    }

    [Fact]
    public async Task WorkspaceService_SaveAsync_EnforcesMaxLimit()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();
        const string userId = "user-limit";

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            for (var i = 1; i <= WorkspaceService.MaxSavedPerDatabase; i++)
            {
                db.SavedQueries.Add(new SavedQuery
                {
                    UserId = userId,
                    DatabaseId = dbId,
                    Name = $"Query {i}",
                    SqlText = $"SELECT {i}"
                });
            }
            await db.SaveChangesAsync();
        }

        var failResult = await ctx.Workspace.SaveAsync(userId, dbId, "Query 51", "SELECT 51", null, null);
        Assert.False(failResult.Ok);
        Assert.Contains("Limit of 50 saved queries per database reached", failResult.Error);
    }

    [Fact]
    public async Task WorkspaceService_DeleteAsync_RemovesQuery()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();
        const string userId = "user-del";

        await ctx.Workspace.SaveAsync(userId, dbId, "Query To Delete", "SELECT 1", null, null);
        var list = await ctx.Workspace.SavedAsync(userId, dbId);
        Assert.Single(list);

        await ctx.Workspace.DeleteAsync(userId, list[0].Id);

        var emptyList = await ctx.Workspace.SavedAsync(userId, dbId);
        Assert.Empty(emptyList);
    }

    [Fact]
    public async Task WorkspaceService_HistoryAsync_ReturnsEntriesOrderedDescending()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();
        const string userId = "user-hist";

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.QueryHistory.Add(new QueryHistory { UserId = userId, DatabaseId = dbId, SqlText = "SELECT 1", Outcome = "Success", ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-10) });
            db.QueryHistory.Add(new QueryHistory { UserId = userId, DatabaseId = dbId, SqlText = "SELECT 2", Outcome = "Success", ExecutedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }

        var history = await ctx.Workspace.HistoryAsync(userId, dbId, take: 10);
        Assert.Equal(2, history.Count);
        Assert.Equal("SELECT 2", history[0].SqlText);
        Assert.Equal("SELECT 1", history[1].SqlText);
    }

    // --- RequestService Tests ---

    [Fact]
    public async Task RequestService_SubmitAccessAsync_ValidatesInput()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        // Empty perms
        var r1 = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, [], "Need access", null, 24, false);
        Assert.False(r1.Ok);
        Assert.Contains("Select permissions and provide a justification", r1.Error);

        // Invalid perm code
        var r2 = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, ["INVALID_PERM"], "Need access", null, 24, false);
        Assert.False(r2.Ok);
        Assert.Contains("Unknown permission requested", r2.Error);

        // Duration bounds (< 1 or > 2160 hours)
        var r3 = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, [Perm.Select], "Need access", null, 0, false);
        Assert.False(r3.Ok);
        Assert.Contains("Duration must be between 1 hour and 90 days", r3.Error);

        var r4 = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, [Perm.Select], "Need access", null, 2500, false);
        Assert.False(r4.Ok);
        Assert.Contains("Duration must be between 1 hour and 90 days", r4.Error);
    }

    [Fact]
    public async Task RequestService_SubmitAccessAsync_CreatesRequestAndHistory()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        var result = await ctx.Requests.SubmitAccessAsync(
            RegularActor,
            dbId,
            [Perm.Select, Perm.Insert],
            "Need to analyze quarterly reports",
            "TICKET-1234",
            48,
            false,
            "sales.orders");

        Assert.True(result.Ok);
        var requestId = Guid.Parse(result.Value!);

        await using var db = await ctx.Factory.CreateDbContextAsync();
        var req = await db.AccessRequests.FindAsync(requestId);
        Assert.NotNull(req);
        Assert.Equal(RegularActor.UserId, req.RequesterId);
        Assert.Equal(dbId, req.DatabaseId);
        Assert.Equal("SELECT,INSERT", req.Permissions);
        Assert.Equal("sales", req.Schema);
        Assert.Equal("orders", req.ObjectName);
        Assert.Equal("TICKET-1234", req.Ticket);
        Assert.Equal(48, req.DurationHours);
        Assert.Equal(RequestStatus.Pending, req.Status);

        var history = await db.RequestStatusHistory.Where(h => h.RequestId == requestId).ToListAsync();
        Assert.Single(history);
        Assert.Equal(RequestStatus.Pending, history[0].Status);
        Assert.Equal(RegularActor.UserName, history[0].ChangedBy);
    }

    [Fact]
    public async Task RequestService_ReviewAccessAsync_RejectsSelfApproval_WhenDisabled()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        // Submit as AdminActor
        var submitResult = await ctx.Requests.SubmitAccessAsync(AdminActor, dbId, [Perm.Select], "Self test", null, 12, false);
        var reqId = Guid.Parse(submitResult.Value!);

        // Try to approve self request when AllowSelfApproval is false (default)
        var reviewResult = await ctx.Requests.ReviewAccessAsync(AdminActor, reqId, approve: true, comment: "Approved self", hoursOverride: null);
        Assert.False(reviewResult.Ok);
        Assert.Equal("You cannot review your own request.", reviewResult.Error);
    }

    [Fact]
    public async Task RequestService_ReviewAccessAsync_RequiresCommentOnRejection()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        var submitResult = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, [Perm.Select], "Need access", null, 12, false);
        var reqId = Guid.Parse(submitResult.Value!);

        var reviewResult = await ctx.Requests.ReviewAccessAsync(AdminActor, reqId, approve: false, comment: null, hoursOverride: null);
        Assert.False(reviewResult.Ok);
        Assert.Equal("A comment is required when rejecting.", reviewResult.Error);
    }

    [Fact]
    public async Task RequestService_ReviewAccessAsync_ApprovalCreatesPermissionGrants()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        // Seed user and managed database to satisfy foreign key constraints
        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = RegularActor.UserId,
                UserName = RegularActor.UserName,
                Email = RegularActor.UserName
            });
            db.Users.Add(new ApplicationUser
            {
                Id = AdminActor.UserId,
                UserName = AdminActor.UserName,
                Email = AdminActor.UserName
            });
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = dbId,
                Name = "TestDb",
                Environment = "Production",
                EncryptedConnectionString = ctx.Crypto.Encrypt("Server=localhost;Database=TestDb")
            });
            await db.SaveChangesAsync();
        }

        var submitResult = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, [Perm.Select, Perm.Insert], "Project X", null, 24, false, "dbo.Orders");
        var reqId = Guid.Parse(submitResult.Value!);

        var reviewResult = await ctx.Requests.ReviewAccessAsync(AdminActor, reqId, approve: true, comment: "Looks good", hoursOverride: 48);
        Assert.True(reviewResult.Ok);

        await using var verifyDb = await ctx.Factory.CreateDbContextAsync();
        var req = await verifyDb.AccessRequests.FindAsync(reqId);
        Assert.NotNull(req);
        Assert.Equal(RequestStatus.Approved, req.Status);
        Assert.Equal(AdminActor.UserId, req.ReviewerId);

        var grants = await verifyDb.PermissionGrants.Where(g => g.SourceRequestId == reqId).ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, g =>
        {
            Assert.Equal(RegularActor.UserId, g.UserId);
            Assert.Equal(dbId, g.DatabaseId);
            Assert.Equal(PermissionEffect.Allow, g.Effect);
            Assert.Equal("dbo", g.Schema);
            Assert.Equal("Orders", g.ObjectName);
            Assert.NotNull(g.EndsAt);
        });
    }

    [Fact]
    public async Task RequestService_CancelAsync_CancelsPendingRequest()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        var submitResult = await ctx.Requests.SubmitAccessAsync(RegularActor, dbId, [Perm.Select], "Cancelling this", null, 12, false);
        var reqId = Guid.Parse(submitResult.Value!);

        var cancelResult = await ctx.Requests.CancelAsync(RegularActor, "Access", reqId);
        Assert.True(cancelResult.Ok);

        await using var db = await ctx.Factory.CreateDbContextAsync();
        var req = await db.AccessRequests.FindAsync(reqId);
        Assert.NotNull(req);
        Assert.Equal(RequestStatus.Cancelled, req.Status);

        // Cannot cancel already cancelled request
        var cancelAgain = await ctx.Requests.CancelAsync(RegularActor, "Access", reqId);
        Assert.False(cancelAgain.Ok);
    }

    [Fact]
    public async Task RequestService_SubmitScriptAsync_ValidatesInputAndHardDenials()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        // Empty title
        var r1 = await ctx.Requests.SubmitScriptAsync(RegularActor, dbId, "", "SELECT 1;", "justification", ctx.Gate);
        Assert.False(r1.Ok);
        Assert.Contains("Title, script and justification are required", r1.Error);

        // Destructive script with hard denial
        var r2 = await ctx.Requests.SubmitScriptAsync(RegularActor, dbId, "Kill connections", "KILL 55;", "Maintenance", ctx.Gate);
        Assert.False(r2.Ok);
        Assert.Contains("Script rejected by the SQL gate", r2.Error);

        // Valid script
        var r3 = await ctx.Requests.SubmitScriptAsync(RegularActor, dbId, "Monthly Report", "SELECT Id, Total FROM Invoices;", "Reporting", ctx.Gate);
        Assert.True(r3.Ok);
        var scriptId = Guid.Parse(r3.Value!);

        await using var db = await ctx.Factory.CreateDbContextAsync();
        var scriptReq = await db.ScriptRequests.FindAsync(scriptId);
        Assert.NotNull(scriptReq);
        Assert.Equal("Monthly Report", scriptReq.Title);
        Assert.Equal(RequestStatus.Pending, scriptReq.Status);
    }

    [Fact]
    public async Task RequestService_ReviewScriptAsync_ValidatesAndApproves()
    {
        await using var ctx = new TestContext();
        var dbId = Guid.NewGuid();

        var submitResult = await ctx.Requests.SubmitScriptAsync(RegularActor, dbId, "Report", "SELECT 1;", "Test", ctx.Gate);
        var scriptId = Guid.Parse(submitResult.Value!);

        // Reject without comment fails
        var rejectNoComment = await ctx.Requests.ReviewScriptAsync(AdminActor, scriptId, approve: false, comment: null, modifiedScript: null, autoRun: false, ctx.Gate);
        Assert.False(rejectNoComment.Ok);

        // Approve with modified script that violates gate fails
        var approveBadModified = await ctx.Requests.ReviewScriptAsync(AdminActor, scriptId, approve: true, comment: "OK", modifiedScript: "SHUTDOWN;", autoRun: false, ctx.Gate);
        Assert.False(approveBadModified.Ok);

        // Valid approve
        var approveResult = await ctx.Requests.ReviewScriptAsync(AdminActor, scriptId, approve: true, comment: "Approved for execution", modifiedScript: "SELECT 2;", autoRun: true, ctx.Gate);
        Assert.True(approveResult.Ok);

        await using var db = await ctx.Factory.CreateDbContextAsync();
        var req = await db.ScriptRequests.FindAsync(scriptId);
        Assert.NotNull(req);
        Assert.Equal(RequestStatus.Approved, req.Status);
        Assert.Equal("SELECT 2;", req.ModifiedScriptText);
        Assert.False(req.AutoRun); // autoRun is disabled if script was modified
    }

    // --- NotificationService Tests ---

    [Fact]
    public async Task NotificationService_NotifyAsync_And_MarkReadAsync()
    {
        await using var ctx = new TestContext();
        const string userId = "notify-user-1";

        // Subscribe to event
        var eventFired = false;
        var unreadEventCount = -1;
        ctx.Notifications.UnreadChanged += (uid, count) =>
        {
            if (uid == userId)
            {
                eventFired = true;
                unreadEventCount = count;
            }
        };

        await ctx.Notifications.NotifyAsync(userId, "System Alert", "Database is reaching storage threshold.", "/admin", broadcastWebhook: false);

        Assert.True(eventFired);
        Assert.Equal(1, unreadEventCount);

        var count = await ctx.Notifications.UnreadCountAsync(userId);
        Assert.Equal(1, count);

        var list = await ctx.Notifications.ListAsync(userId);
        Assert.Single(list);
        Assert.Equal("System Alert", list[0].Title);
        Assert.False(list[0].IsRead);

        // Mark read
        await ctx.Notifications.MarkReadAsync(userId, list[0].Id);
        var unreadAfter = await ctx.Notifications.UnreadCountAsync(userId);
        Assert.Equal(0, unreadAfter);
    }

    [Fact]
    public async Task NotificationService_NotifyRoleAsync_DispatchesToRoleMembers()
    {
        await using var ctx = new TestContext();

        await using (var db = await ctx.Factory.CreateDbContextAsync())
        {
            var role = new IdentityRole { Id = "role-dba", Name = "DBA", NormalizedName = "DBA" };
            db.Roles.Add(role);

            var user1 = new ApplicationUser { Id = "dba-1", UserName = "dba1@test.com", Email = "dba1@test.com", IsActive = true };
            var user2 = new ApplicationUser { Id = "dba-2", UserName = "dba2@test.com", Email = "dba2@test.com", IsActive = true };
            var userInactive = new ApplicationUser { Id = "dba-inactive", UserName = "dba_old@test.com", Email = "dba_old@test.com", IsActive = false };

            db.Users.AddRange(user1, user2, userInactive);
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = "dba-1", RoleId = "role-dba" });
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = "dba-2", RoleId = "role-dba" });
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = "dba-inactive", RoleId = "role-dba" });
            await db.SaveChangesAsync();
        }

        await ctx.Notifications.NotifyRoleAsync("DBA", "Failover Alert", "Primary node failover complete.", exceptUserId: "dba-2");

        var dba1Count = await ctx.Notifications.UnreadCountAsync("dba-1");
        var dba2Count = await ctx.Notifications.UnreadCountAsync("dba-2");
        var inactiveCount = await ctx.Notifications.UnreadCountAsync("dba-inactive");

        Assert.Equal(1, dba1Count);
        Assert.Equal(0, dba2Count); // Excluded via exceptUserId
        Assert.Equal(0, inactiveCount); // Inactive user excluded
    }

    [Fact]
    public async Task NotificationService_SendTestEmailAsync_ValidatesInputAndConfiguration()
    {
        await using var ctx = new TestContext();

        var emptyRecipient = await ctx.Notifications.SendTestEmailAsync("");
        Assert.False(emptyRecipient.Ok);
        Assert.Equal("Recipient email address is required.", emptyRecipient.Error);

        var unconfiguredSmtp = await ctx.Notifications.SendTestEmailAsync("admin@example.com");
        Assert.False(unconfiguredSmtp.Ok);
        Assert.Contains("SMTP server is not configured", unconfiguredSmtp.Error);

        var invalidRecipient = await ctx.Notifications.SendTestEmailAsync("not-an-email");
        Assert.False(invalidRecipient.Ok);
        Assert.Contains("not a valid email address", invalidRecipient.Error);
    }

    [Fact]
    public async Task SettingsService_And_NotificationService_SmtpFromAndSslHandling()
    {
        await using var ctx = new TestContext();

        // Saving with EnableSsl = false and empty From falls back to valid email User
        await ctx.Settings.SaveSmtpConfigAsync(ctx.Crypto, "smtp.gmail.com", 587, enableSsl: false, "arjun1108@gmail.com", "secret", "");
        var cfg1 = await ctx.Settings.GetSmtpConfigAsync(ctx.Crypto);
        Assert.True(cfg1.IsConfigured);
        Assert.False(cfg1.EnableSsl);
        Assert.Equal("arjun1108@gmail.com", cfg1.From);

        // Saving with EnableSsl = true and non-email User falls back to noreply@localhost
        await ctx.Settings.SaveSmtpConfigAsync(ctx.Crypto, "smtp.example.com", 25, enableSsl: true, "smtpuser", null, "");
        var cfg2 = await ctx.Settings.GetSmtpConfigAsync(ctx.Crypto);
        Assert.True(cfg2.EnableSsl);
        Assert.Equal("noreply@localhost", cfg2.From);

        // Pre-existing empty Smtp:From in DB still resolves cleanly
        await ctx.Settings.SetRawAsync("Smtp:From", "");
        var cfg3 = await ctx.Settings.GetSmtpConfigAsync(ctx.Crypto);
        Assert.Equal("noreply@localhost", cfg3.From);

        // Pre-existing invalid Smtp:From returns a friendly OpResult error instead of FormatException
        await ctx.Settings.SetRawAsync("Smtp:From", "invalid sender");
        var res = await ctx.Notifications.SendTestEmailAsync("arjun1108@gmail.com");
        Assert.False(res.Ok);
        Assert.Contains("Sender address (From)", res.Error);
        Assert.Contains("not a valid email address", res.Error);
    }

    // --- UserAdminService Tests ---

    [Fact]
    public void UserAdminService_GeneratePassword_MeetsComplexityRules()
    {
        var password16 = UserAdminService.GeneratePassword(16);
        Assert.Equal(16, password16.Length);
        Assert.Contains(password16, char.IsUpper);
        Assert.Contains(password16, char.IsLower);
        Assert.Contains(password16, char.IsDigit);
        Assert.Contains(password16, c => "!@#$%^&*-_=+".Contains(c));

        var password24 = UserAdminService.GeneratePassword(24);
        Assert.Equal(24, password24.Length);
    }

    [Fact]
    public async Task UserAdminService_SetActiveAsync_PreventsSelfDeactivation()
    {
        await using var ctx = new TestContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ctx.Factory);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var userAdmin = new UserAdminService(ctx.Factory, scopeFactory, ctx.Audit, null!);

        var result = await userAdmin.SetActiveAsync(AdminActor, AdminActor.UserId, active: false);
        Assert.False(result.Ok);
        Assert.Equal("You cannot deactivate your own account.", result.Error);
    }

    [Fact]
    public async Task UserAdminService_SetRoleAsync_PreventsSelfAdminRemoval()
    {
        await using var ctx = new TestContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ctx.Factory);
        services.AddScoped(_ => ctx.Factory.CreateDbContext());
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Seed roles and user
        using (var scope = scopeFactory.CreateScope())
        {
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await roleMgr.CreateAsync(new IdentityRole("Developer"));
            await roleMgr.CreateAsync(new IdentityRole(Roles.Admin));
            var user = new ApplicationUser
            {
                Id = AdminActor.UserId,
                UserName = AdminActor.UserName,
                Email = AdminActor.UserName
            };
            await userMgr.CreateAsync(user);
        }

        var userAdmin = new UserAdminService(ctx.Factory, scopeFactory, ctx.Audit, null!);

        var result = await userAdmin.SetRoleAsync(AdminActor, AdminActor.UserId, role: "Developer");
        Assert.False(result.Ok);
        Assert.Equal("You cannot remove your own Admin role.", result.Error);
    }
}
