using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.RateLimiting;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DbAccessManager.Tests;

public class MinimalApiIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient? _client;
    private SqliteConnection? _connection;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddMemoryCache();
        services.AddLogging();
        services.AddHttpContextAccessor();

        services.AddDbContextFactory<ApplicationDbContext>(opt => opt.UseSqlite(_connection));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

        services.AddIdentityCore<ApplicationUser>(opt =>
        {
            opt.Password.RequireDigit = false;
            opt.Password.RequiredLength = 4;
            opt.Password.RequireNonAlphanumeric = false;
            opt.Password.RequireUppercase = false;
        })
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        var jwtKey = TokenService.BuildKey(builder.Configuration, builder.Environment);
        services.AddSingleton(jwtKey);
        services.AddSingleton<ITenantLookupCache, TenantLookupCache>();
        services.AddSingleton<IDatabaseCache, DatabaseCache>();
        services.AddSingleton<IUserStatusCache, UserStatusCache>();
        services.AddSingleton<PermissionResolver>();
        services.AddSingleton<SqlGate>();
        services.AddSingleton<CryptoService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AuditService>();
        services.AddSingleton<PermissionService>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<DatabaseProviderFactory>();
        services.AddSingleton(sp => new TokenService(jwtKey, sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = TokenService.Issuer,
                    ValidateAudience = true,
                    ValidAudience = TokenService.Issuer,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwtKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                options.Events = new JwtBearerEvents
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

        services.AddAuthorization();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("auth", http => RateLimitPartition.GetNoLimiter("anonymous"));
        });

        _app = builder.Build();

        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseAuthorization();

        _app.MapApi();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = "test-user-id",
                UserName = "apiuser@example.com",
                Email = "apiuser@example.com",
                DisplayName = "Api User",
                IsActive = true,
                EmailConfirmed = true
            };
            await userManager.CreateAsync(user, "Password123!");

            var inactiveUser = new ApplicationUser
            {
                Id = "inactive-user-id",
                UserName = "inactive@example.com",
                Email = "inactive@example.com",
                DisplayName = "Inactive User",
                IsActive = false,
                EmailConfirmed = true
            };
            await userManager.CreateAsync(inactiveUser, "Password123!");
        }

        await _app.StartAsync();

        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        _baseUrl = addresses?.FirstOrDefault() ?? "http://127.0.0.1:5000";

        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401()
    {
        var response = await _client!.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "apiuser@example.com",
            password = "WrongPassword"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_InactiveUser_Returns401()
    {
        var response = await _client!.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "inactive@example.com",
            password = "Password123!"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200AndSetsCookie()
    {
        var response = await _client!.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "apiuser@example.com",
            password = "Password123!"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(content);
        Assert.False(string.IsNullOrWhiteSpace(content.AccessToken));
        Assert.True(content.ExpiresInSeconds > 0);
        Assert.False(content.MustChangePassword);

        // Verify Set-Cookie header contains dbam_refresh
        Assert.True(response.Headers.Contains("Set-Cookie"));
        var cookies = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("dbam_refresh", cookies);
    }

    [Fact]
    public async Task Refresh_WithoutCookie_Returns401()
    {
        var response = await _client!.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithValidCookie_ReturnsNewAccessToken()
    {
        // First login to obtain cookie
        var loginResp = await _client!.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "apiuser@example.com",
            password = "Password123!"
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        Assert.True(loginResp.Headers.TryGetValues("Set-Cookie", out var cookieValues));
        var cookieHeader = cookieValues!.First().Split(';')[0];
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", cookieHeader);

        var refreshResp = await _client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);

        var content = await refreshResp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(content);
        Assert.False(string.IsNullOrWhiteSpace(content.AccessToken));
    }

    [Fact]
    public async Task Logout_ClearsCookieAndRevokes()
    {
        // Login first
        var loginResp = await _client!.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "apiuser@example.com",
            password = "Password123!"
        });

        Assert.True(loginResp.Headers.TryGetValues("Set-Cookie", out var cookieValues));
        var cookieHeader = cookieValues!.First().Split(';')[0];
        var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutReq.Headers.Add("Cookie", cookieHeader);

        var logoutResp = await _client!.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        // Subsequent refresh with same cookie should fail (revoked)
        var refreshReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshReq.Headers.Add("Cookie", cookieHeader);
        var refreshResp = await _client!.SendAsync(refreshReq);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);
    }

    [Fact]
    public async Task SecuredEndpoints_WithoutToken_Return401()
    {
        var dbResp = await _client!.GetAsync("/api/v1/databases");
        Assert.Equal(HttpStatusCode.Unauthorized, dbResp.StatusCode);

        var effResp = await _client.GetAsync($"/api/v1/access/effective/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, effResp.StatusCode);

        var queryResp = await _client.PostAsJsonAsync("/api/v1/query/execute", new
        {
            databaseId = Guid.NewGuid(),
            sql = "SELECT 1"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, queryResp.StatusCode);
    }

    [Fact]
    public async Task SecuredEndpoints_WithValidToken_SucceedsOrResolvesSafely()
    {
        // Login to get access token
        var loginResp = await _client!.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "apiuser@example.com",
            password = "Password123!"
        });
        var content = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(content);

        var authedClient = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        authedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", content!.AccessToken);

        // Databases endpoint
        var dbResp = await authedClient.GetAsync("/api/v1/databases");
        Assert.Equal(HttpStatusCode.OK, dbResp.StatusCode);

        // Query execute on unknown db returns 403 Forbidden (denied because database doesn't exist)
        var queryResp = await authedClient.PostAsJsonAsync("/api/v1/query/execute", new
        {
            databaseId = Guid.NewGuid(),
            sql = "SELECT 1"
        });
        Assert.Equal(HttpStatusCode.Forbidden, queryResp.StatusCode);
    }

    private sealed record LoginResponse(string AccessToken, int ExpiresInSeconds, bool MustChangePassword);
}
