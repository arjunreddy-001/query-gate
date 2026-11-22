using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DbAccessManager.Data;
using DbAccessManager.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DbAccessManager.Tests;

public class TokenServiceTests
{
    private static (TokenService Service, SqliteConnection Connection, IDbContextFactory<ApplicationDbContext> Factory) CreateService()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var initialDb = new ApplicationDbContext(options))
        {
            initialDb.Database.EnsureCreated();
        }

        var factory = new TestDbContextFactory(options);
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokenGenerationAndSigning12345!"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var key = TokenService.BuildKey(config);
        var service = new TokenService(key, factory);

        return (service, connection, factory);
    }

    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    [Fact]
    public void BuildKey_ReturnsValidSymmetricSecurityKey()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "CustomSecureKeyForTestingSigningOperations1234567890"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
        var key = TokenService.BuildKey(config);

        Assert.NotNull(key);
        Assert.NotNull(key.Key);
        Assert.True(key.KeySize >= 256);
    }

    [Fact]
    public void CreateAccessToken_GeneratesValidJwtWithExpectedClaims()
    {
        var (service, connection, _) = CreateService();
        using (connection)
        {
            var user = new ApplicationUser
            {
                Id = "user-123",
                UserName = "alice",
                Email = "alice@example.com"
            };

            var roles = new[] { "Admin", "Developer" };
            var tenantId = Guid.NewGuid();
            var tenantSlug = "acme-corp";

            var tokenString = service.CreateAccessToken(user, roles, tenantSlug, tenantId);
            Assert.False(string.IsNullOrWhiteSpace(tokenString));

            var handler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = TokenService.Issuer,
                ValidateAudience = true,
                ValidAudience = TokenService.Issuer,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = service.Key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            var principal = handler.ValidateToken(tokenString, validationParameters, out var validatedToken);
            Assert.NotNull(principal);
            Assert.NotNull(validatedToken);

            var jwt = Assert.IsType<JwtSecurityToken>(validatedToken);
            Assert.Equal(TokenService.Issuer, jwt.Issuer);

            Assert.Equal("user-123", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
            Assert.Equal("alice", principal.FindFirst(ClaimTypes.Name)?.Value);
            Assert.Equal(tenantSlug, principal.FindFirst("tenant_slug")?.Value);
            Assert.Equal(tenantId.ToString(), principal.FindFirst("tenant_id")?.Value);

            var principalRoles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
            Assert.Contains("Admin", principalRoles);
            Assert.Contains("Developer", principalRoles);
        }
    }

    [Fact]
    public async Task CreateRefreshTokenAsync_SavesHashedTokenInDatabase()
    {
        var (service, connection, factory) = CreateService();
        using (connection)
        {
            const string userId = "user-abc";
            var rawToken = await service.CreateRefreshTokenAsync(userId);

            Assert.False(string.IsNullOrWhiteSpace(rawToken));

            await using var db = await factory.CreateDbContextAsync();
            var expectedHash = CryptoService.Sha256(rawToken);
            var record = await db.RefreshTokens.FirstOrDefaultAsync(t => t.UserId == userId);

            Assert.NotNull(record);
            Assert.Equal(expectedHash, record.TokenHash);
            Assert.Null(record.RevokedAt);
            Assert.True(record.ExpiresAt > DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task RotateAsync_WithValidToken_RotatesAndReplacesToken()
    {
        var (service, connection, factory) = CreateService();
        using (connection)
        {
            const string userId = "user-rotate";
            var rawToken = await service.CreateRefreshTokenAsync(userId);

            var rotationResult = await service.RotateAsync(rawToken);
            Assert.NotNull(rotationResult);
            Assert.Equal(userId, rotationResult.Value.UserId);
            Assert.NotEqual(rawToken, rotationResult.Value.NewRefresh);

            await using var db = await factory.CreateDbContextAsync();
            var oldHash = CryptoService.Sha256(rawToken);
            var oldRecord = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == oldHash);
            Assert.NotNull(oldRecord);
            Assert.NotNull(oldRecord.RevokedAt);

            var newHash = CryptoService.Sha256(rotationResult.Value.NewRefresh);
            Assert.Equal(newHash, oldRecord.ReplacedByHash);

            var newRecord = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == newHash);
            Assert.NotNull(newRecord);
            Assert.Null(newRecord.RevokedAt);
        }
    }

    [Fact]
    public async Task RotateAsync_ReusingRevokedToken_TriggersFamilyRevocation()
    {
        var (service, connection, factory) = CreateService();
        using (connection)
        {
            const string userId = "user-family-revocation";
            var token1 = await service.CreateRefreshTokenAsync(userId);
            var token2 = await service.CreateRefreshTokenAsync(userId);

            // First rotation is valid
            var rotation1 = await service.RotateAsync(token1);
            Assert.NotNull(rotation1);

            // Attacker or replay tries to reuse token1 which is now revoked
            var replayResult = await service.RotateAsync(token1);
            Assert.Null(replayResult);

            // All tokens for the user must now be revoked
            await using var db = await factory.CreateDbContextAsync();
            var activeCount = await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null);
            Assert.Equal(0, activeCount);
        }
    }

    [Fact]
    public async Task RotateAsync_NonExistentOrExpiredToken_ReturnsNull()
    {
        var (service, connection, factory) = CreateService();
        using (connection)
        {
            // Non-existent token
            var resultNonExistent = await service.RotateAsync("non-existent-token-12345");
            Assert.Null(resultNonExistent);

            // Expired token
            const string userId = "user-expired";
            var rawToken = "expired-token-raw-string";
            var hash = CryptoService.Sha256(rawToken);
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.RefreshTokens.Add(new RefreshToken
                {
                    UserId = userId,
                    TokenHash = hash,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                });
                await db.SaveChangesAsync();
            }

            var resultExpired = await service.RotateAsync(rawToken);
            Assert.Null(resultExpired);
        }
    }

    [Fact]
    public async Task RevokeAsync_MarksTokenRevoked()
    {
        var (service, connection, factory) = CreateService();
        using (connection)
        {
            const string userId = "user-revoke";
            var rawToken = await service.CreateRefreshTokenAsync(userId);

            await service.RevokeAsync(rawToken);

            await using var db = await factory.CreateDbContextAsync();
            var hash = CryptoService.Sha256(rawToken);
            var record = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);

            Assert.NotNull(record);
            Assert.NotNull(record.RevokedAt);
        }
    }
}
