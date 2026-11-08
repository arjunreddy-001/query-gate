using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbAccessManager.Tests;

public class SaveGrantsTests
{
    private static readonly Actor Actor = new("user1", "alice", "127.0.0.1", "TestAgent");

    private static AccessConfigurationService CreateService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseSqlServer("Server=dummy;Database=dummy;TrustServerCertificate=True;"));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddScoped<AuditService>();
        services.AddScoped<AccessConfigurationService>();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<AccessConfigurationService>();
    }

    [Fact]
    public async Task SaveGrants_Fails_WhenNeitherRoleNorUserSpecified()
    {
        var svc = CreateService();
        var result = await svc.SaveGrantsAsync(
            Actor,
            Guid.NewGuid(),
            roleId: null,
            userId: null,
            finalPermissions: ["SELECT"],
            initialPermissions: [],
            effect: PermissionEffect.Allow);

        Assert.False(result.Ok);
        Assert.Contains("Select either a role or a user", result.Error);
    }

    [Fact]
    public async Task SaveGrants_Fails_WhenBothRoleAndUserSpecified()
    {
        var svc = CreateService();
        var result = await svc.SaveGrantsAsync(
            Actor,
            Guid.NewGuid(),
            roleId: "role1",
            userId: "user1",
            finalPermissions: ["SELECT"],
            initialPermissions: [],
            effect: PermissionEffect.Allow);

        Assert.False(result.Ok);
        Assert.Contains("Cannot assign grant to both", result.Error);
    }

    [Fact]
    public async Task SaveGrants_Fails_WhenPermissionIsInvalid()
    {
        var svc = CreateService();
        var result = await svc.SaveGrantsAsync(
            Actor,
            Guid.NewGuid(),
            roleId: "role1",
            userId: null,
            finalPermissions: ["INVALID_HACK"],
            initialPermissions: [],
            effect: PermissionEffect.Allow);

        Assert.False(result.Ok);
        Assert.Contains("Invalid permission code", result.Error);
    }

    [Fact]
    public async Task SaveGrants_Fails_WhenEndTimeBeforeStartTime()
    {
        var svc = CreateService();
        var result = await svc.SaveGrantsAsync(
            Actor,
            Guid.NewGuid(),
            roleId: "role1",
            userId: null,
            finalPermissions: ["SELECT"],
            initialPermissions: [],
            effect: PermissionEffect.Allow,
            starts: new DateTime(2026, 11, 8, 12, 0, 0),
            ends: new DateTime(2026, 11, 8, 11, 0, 0));

        Assert.False(result.Ok);
        Assert.Contains("End time must be after start time", result.Error);
    }

    [Theory]
    [InlineData(Perm.DynamicSql, true)]
    [InlineData(Perm.Unmask, true)]
    [InlineData(Perm.LinkedServer, true)]
    [InlineData(Perm.XpCmdShell, true)]
    [InlineData(Perm.Select, false)]
    [InlineData(Perm.Insert, false)]
    [InlineData(Perm.Export, false)]
    public void HighRisk_Identifies_CriticalPermissions(string code, bool expectedHighRisk)
    {
        Assert.Equal(expectedHighRisk, Perm.IsHighRisk(code));
        if (expectedHighRisk)
        {
            var reason = Perm.HighRiskReason(code);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public void ExportPermission_Description_ClarifiesWorkspaceScope()
    {
        var desc = Perm.Description(Perm.Export);
        Assert.Contains("query workbench", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CSV", desc);
        Assert.Contains("XLSX", desc);
        Assert.Contains("XLSB", desc);
    }

    [Theory]
    [InlineData("VIEW DEFINITION", Perm.ViewDefinition)]
    [InlineData("view definition", Perm.ViewDefinition)]
    [InlineData("VIEW_DEFINITION", Perm.ViewDefinition)]
    [InlineData("DYNAMIC SQL", Perm.DynamicSql)]
    [InlineData("dynamic_sql", Perm.DynamicSql)]
    [InlineData("XP CMDSHELL", Perm.XpCmdShell)]
    [InlineData("xp_cmdshell", Perm.XpCmdShell)]
    [InlineData("LINKED SERVER", Perm.LinkedServer)]
    [InlineData("linked_server", Perm.LinkedServer)]
    [InlineData("SELECT", Perm.Select)]
    [InlineData("select", Perm.Select)]
    [InlineData("sysproc:sp_who2", "SYSPROC:SP_WHO2")]
    public void PermNormalize_Handles_Variations(string input, string expected)
    {
        var normalized = Perm.Normalize(input);
        Assert.Equal(expected, normalized);
        Assert.True(Perm.IsValid(normalized));
        Assert.True(Perm.IsValid(input));
    }

    [Theory]
    [InlineData(Perm.DynamicSql)]
    [InlineData(Perm.Unmask)]
    [InlineData(Perm.LinkedServer)]
    [InlineData(Perm.XpCmdShell)]
    public void Dangerous_And_HighRisk_Are_Synchronized_At_Source(string code)
    {
        Assert.True(Perm.Dangerous.Contains(code), $"{code} must be in Perm.Dangerous");
        Assert.True(Perm.IsDangerous(code), $"{code} must return true for Perm.IsDangerous");
        Assert.True(Perm.HighRisk.Contains(code), $"{code} must be in Perm.HighRisk");
        Assert.True(Perm.IsHighRisk(code), $"{code} must return true for Perm.IsHighRisk");
    }

    [Theory]
    [InlineData("Csv", "ExportCsv")]
    [InlineData("Xlsx", "ExportXlsx")]
    [InlineData("Xlsb", "ExportXlsb")]
    [InlineData("Json", "ExportJson")]
    [InlineData("Markdown", "ExportMarkdown")]
    [InlineData("SqlInsert", "ExportSqlInsert")]
    public void ExportFormat_ActionNaming_IsConsistent(string format, string expectedAction)
    {
        var action = $"Export{format}";
        Assert.Equal(expectedAction, action);
    }
}
