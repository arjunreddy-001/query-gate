using System.Security.Claims;
using System.Threading.RateLimiting;
using DbAccessManager.Components;
using DbAccessManager.Components.Account;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

var dataProtectionPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("QueryGate");
}

var jwtKey = TokenService.BuildKey(builder.Configuration, builder.Environment);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.AddAuthentication()
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = TokenService.Issuer,
            ValidateAudience = true, ValidAudience = TokenService.Issuer,
            ValidateIssuerSigningKey = true, IssuerSigningKey = jwtKey,
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (id is null) { context.Fail("Invalid token principal."); return; }
                var userCache = context.HttpContext.RequestServices.GetRequiredService<IUserStatusCache>();
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var status = await userCache.GetOrLoadAsync(id, users);
                if (status is null || !status.CanSignIn(DateTimeOffset.UtcNow)) context.Fail("Account is inactive.");
            }
        };
    });

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure()));
builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>, TenantAwareDbContextFactory>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, BcryptPasswordHasher<ApplicationUser>>();
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddHealthChecks();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    var original = options.Events.OnValidatePrincipal;
    options.Events.OnValidatePrincipal = async context =>
    {
        await original(context);
        if (context.Principal is null) return;
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(context.Principal);
        if (user is null || !user.CanSignIn(DateTimeOffset.UtcNow))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddSingleton<ITenantLookupCache, TenantLookupCache>();
builder.Services.AddSingleton<IDatabaseCache, DatabaseCache>();
builder.Services.AddSingleton<IUserStatusCache, UserStatusCache>();
builder.Services.AddSingleton<PermissionResolver>();
builder.Services.AddSingleton<SqlGate>();
builder.Services.AddSingleton<CryptoService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<AppLogService>();
builder.Services.AddSingleton<ILoggerProvider, AppDbLoggerProvider>();
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddSingleton<AccessConfigurationService>();
builder.Services.AddSingleton<IAuthorizationHandler, AuditAuthorizationHandler>();
builder.Services.AddAuthorization(options => options.AddPolicy("AuditAccess", policy =>
    policy.RequireAuthenticatedUser().AddRequirements(new AuditRequirement())));
builder.Services.AddSingleton<ExecutionService>();
builder.Services.AddSingleton<MetadataService>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<DatabaseProviderFactory>();
builder.Services.AddSingleton<DatabaseAdminService>();
builder.Services.AddSingleton<UserAdminService>();
builder.Services.AddSingleton<RequestService>();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton(sp => new TokenService(jwtKey, sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()));
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddScoped<CircuitHandler, TenantCircuitHandler>();
builder.Services.AddScoped<ClientInfo>();
builder.Services.AddScoped<CircuitHandler, ClientInfoCircuitHandler>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddSingleton<HealthMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorService>());

var app = builder.Build();

var cspHeader = builder.Configuration["Security:ContentSecurityPolicy"] ??
    "default-src 'self'; script-src 'self' 'unsafe-eval' 'unsafe-inline' https://static.cloudflareinsights.com; " +
    "style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
    "connect-src 'self' ws: wss: https://cloudflareinsights.com; worker-src 'self' blob: data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

app.Use(async (context, next) =>
{
    context.TraceIdentifier = Guid.NewGuid().ToString("N")[..12];
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = cspHeader;
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        try
        {
            var appLog = context.RequestServices.GetService<AppLogService>();
            if (appLog != null)
            {
                await appLog.LogErrorAsync(
                    ex.Message,
                    ex,
                    context.User.Identity?.Name,
                    context.Request.Path,
                    correlationId: context.TraceIdentifier);
            }
        }
        catch { /* logging must never mask the original failure */ }
        throw;
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHealthChecks("/health");
app.MapAdditionalIdentityEndpoints();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.MigrateAsync();
}

await SeedAsync(app.Services, app.Configuration);

await app.RunAsync();

static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
{
    await using var scope = services.CreateAsyncScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { Roles.Admin, Roles.SuperAdmin })
    {
        if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new IdentityRole(role));
    }

    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var defaultTenant = await dbContext.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Slug == "default");
    if (defaultTenant is null)
    {
        defaultTenant = new Tenant
        {
            Name = "Default Workspace",
            Slug = "default",
            IsActive = true
        };
        dbContext.Tenants.Add(defaultTenant);
        await dbContext.SaveChangesAsync();

        // Backfill any existing unassigned records to the default tenant
        await dbContext.ManagedDatabases.IgnoreQueryFilters().Where(d => d.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(d => d.TenantId, defaultTenant.Id));
        await dbContext.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(a => a.TenantId, defaultTenant.Id));
        await dbContext.AppLogs.IgnoreQueryFilters().Where(a => a.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(a => a.TenantId, defaultTenant.Id));
        await dbContext.SavedQueries.IgnoreQueryFilters().Where(q => q.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(q => q.TenantId, defaultTenant.Id));
        await dbContext.QueryHistory.IgnoreQueryFilters().Where(q => q.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(q => q.TenantId, defaultTenant.Id));
        await dbContext.AccessRequests.IgnoreQueryFilters().Where(r => r.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(r => r.TenantId, defaultTenant.Id));
        await dbContext.ScriptRequests.IgnoreQueryFilters().Where(r => r.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(r => r.TenantId, defaultTenant.Id));
        await dbContext.RequestStatusHistory.IgnoreQueryFilters().Where(r => r.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(r => r.TenantId, defaultTenant.Id));
        await dbContext.Notifications.IgnoreQueryFilters().Where(n => n.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(n => n.TenantId, defaultTenant.Id));
        await dbContext.Settings.IgnoreQueryFilters().Where(st => st.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(st => st.TenantId, defaultTenant.Id));
        await dbContext.DatabaseEnvironments.IgnoreQueryFilters().Where(e => e.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(e => e.TenantId, defaultTenant.Id));
        await dbContext.EnvironmentDefaultGrants.IgnoreQueryFilters().Where(g => g.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(g => g.TenantId, defaultTenant.Id));
        await dbContext.DatabaseRoleScopeRules.IgnoreQueryFilters().Where(r => r.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(r => r.TenantId, defaultTenant.Id));
        await dbContext.PermissionGrants.IgnoreQueryFilters().Where(g => g.TenantId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(g => g.TenantId, defaultTenant.Id));
    }

    TenantContextAccessor.SetFallbackDefaultTenant(defaultTenant.Id, defaultTenant.Slug, defaultTenant.Name);

    var email = configuration["BootstrapAdmin:Email"];
    var password = configuration["BootstrapAdmin:Password"];
    if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password)) return;
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
    {
        throw new InvalidOperationException("Both BootstrapAdmin:Email and BootstrapAdmin:Password must be configured together.");
    }

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var user = await userManager.FindByEmailAsync(email);
    if (user is null)
    {
        user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator", IsActive = true };
        var userResult = await userManager.CreateAsync(user, password);
        if (!userResult.Succeeded)
        {
            throw new InvalidOperationException($"The bootstrap administrator could not be created: {string.Join(", ", userResult.Errors.Select(e => e.Code))}.");
        }
    }

    if (!await userManager.IsInRoleAsync(user, Roles.Admin))
    {
        var roleResult = await userManager.AddToRoleAsync(user, Roles.Admin);
        if (!roleResult.Succeeded) throw new InvalidOperationException("The bootstrap administrator role could not be assigned.");
    }

    if (!await userManager.IsInRoleAsync(user, Roles.SuperAdmin))
    {
        await userManager.AddToRoleAsync(user, Roles.SuperAdmin);
    }

    if (!await dbContext.UserTenantMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == user.Id && m.TenantId == defaultTenant.Id))
    {
        dbContext.UserTenantMemberships.Add(new UserTenantMembership
        {
            UserId = user.Id,
            TenantId = defaultTenant.Id,
            Role = Roles.Admin
        });
        await dbContext.SaveChangesAsync();
    }

    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("QueryGate.Bootstrap");
    logger.LogInformation("Bootstrap administrator account ready: {Email}", email);
}
