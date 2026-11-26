using System.Security.Claims;
using Bunit;
using DbAccessManager.Components.Account.Pages;
using DbAccessManager.Components.Account.Shared;
using DbAccessManager.Components.Pages;
using DbAccessManager.Components.Pages.Admin;
using DbAccessManager.Components.Shared;
using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Ai;
using DbAccessManager.Services.Providers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbAccessManager.Tests;

public class BlazorComponentTests : BunitContext
{
    [Fact]
    public void Icon_RendersSvgWithGivenNameAndAttributes()
    {
        var cut = Render<Icon>(parameters => parameters
            .Add(p => p.Name, "db")
            .Add(p => p.Class, "test-icon-class")
            .Add(p => p.Style, "color:red;")
        );

        var svg = cut.Find("svg");
        Assert.NotNull(svg);
        Assert.Contains("test-icon-class", svg.GetAttribute("class"));
        Assert.Contains("color:red;", svg.GetAttribute("style"));
        Assert.Contains("<ellipse", cut.Markup);
    }

    [Fact]
    public void Icon_UnknownName_RendersEmptySvg()
    {
        var cut = Render<Icon>(parameters => parameters
            .Add(p => p.Name, "non_existent_icon_name")
        );

        var svg = cut.Find("svg");
        Assert.NotNull(svg);
        Assert.Empty(svg.Children);
    }

    [Theory]
    [InlineData("panel-bottom")]
    [InlineData("panel-right")]
    public void Icon_WorkspacePanelIcons_RenderSvgGeometry(string iconName)
    {
        var cut = Render<Icon>(parameters => parameters
            .Add(p => p.Name, iconName)
        );

        var svg = cut.Find("svg");
        Assert.NotNull(svg);
        Assert.NotEmpty(svg.Children);
    }

    [Fact]
    public void ConfirmDialog_WhenClosed_RendersNothing()
    {
        var cut = Render<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsOpen, false)
            .Add(p => p.Title, "Delete Item")
            .Add(p => p.Message, "Are you sure?")
        );

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void ConfirmDialog_WhenOpen_RendersDetailsAndButtons()
    {
        var confirmed = false;
        var cancelled = false;

        var cut = Render<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "Drop Database")
            .Add(p => p.Message, "This action cannot be undone.")
            .Add(p => p.Details, "All tables will be deleted permanently.")
            .Add(p => p.ConfirmText, "Yes, drop")
            .Add(p => p.CancelText, "No, keep")
            .Add(p => p.IsDanger, true)
            .Add(p => p.OnConfirm, () => { confirmed = true; })
            .Add(p => p.OnCancel, () => { cancelled = true; })
        );

        Assert.Contains("Drop Database", cut.Markup);
        Assert.Contains("This action cannot be undone.", cut.Markup);
        Assert.Contains("All tables will be deleted permanently.", cut.Markup);

        var buttons = cut.FindAll("button");
        Assert.Equal(2, buttons.Count);

        var cancelButton = buttons[0];
        Assert.Equal("No, keep", cancelButton.TextContent.Trim());
        cancelButton.Click();
        Assert.True(cancelled);

        var confirmButton = buttons[1];
        Assert.Equal("Yes, drop", confirmButton.TextContent.Trim());
        Assert.Contains("btn-danger", confirmButton.ClassName);
        confirmButton.Click();
        Assert.True(confirmed);
    }

    [Fact]
    public void ConfirmDialog_NotDanger_UsesPrimaryButton()
    {
        var cut = Render<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "Save Changes")
            .Add(p => p.Message, "Do you want to apply settings?")
            .Add(p => p.IsDanger, false)
        );

        var confirmButton = cut.FindAll("button")[1];
        Assert.Contains("btn-primary", confirmButton.ClassName);
        Assert.DoesNotContain("btn-danger", confirmButton.ClassName);
    }

    [Fact]
    public void ToastHost_InitiallyEmpty_RendersNoToasts()
    {
        Services.AddSingleton<ToastService>();
        var cut = Render<ToastHost>();

        Assert.Empty(cut.FindAll(".toast"));
    }

    [Fact]
    public void ToastHost_DisplaysNotification_AndDismissesOnClick()
    {
        var toastService = new ToastService();
        Services.AddSingleton(toastService);

        var cut = Render<ToastHost>();

        toastService.Show("Data saved successfully", title: "Success");
        cut.WaitForAssertion(() =>
        {
            var toasts = cut.FindAll(".toast");
            Assert.Single(toasts);
            Assert.Contains("Data saved successfully", cut.Markup);
            Assert.Contains("Success", cut.Markup);
        });

        // Add error toast
        toastService.Show("Syntax error at line 4", isError: true, title: "Query Failed");
        cut.WaitForAssertion(() =>
        {
            var toasts = cut.FindAll(".toast");
            Assert.Equal(2, toasts.Count);
            Assert.Contains("toast-bad", cut.Markup);
        });

        // Dismiss first toast
        var dismissButtons = cut.FindAll(".toast-close");
        dismissButtons[0].Click();

        cut.WaitForAssertion(() =>
        {
            var toasts = cut.FindAll(".toast");
            Assert.Single(toasts);
            Assert.DoesNotContain("Data saved successfully", cut.Markup);
            Assert.Contains("Syntax error at line 4", cut.Markup);
        });
    }

    [Fact]
    public void ShowRecoveryCodes_RendersCodesAndWarning()
    {
        var codes = new[] { "ABCD-1234", "EFGH-5678", "IJKL-9012" };
        var httpContext = new DefaultHttpContext();

        var cut = Render<ShowRecoveryCodes>(parameters => parameters
            .AddCascadingValue(httpContext)
            .Add(p => p.RecoveryCodes, codes)
            .Add(p => p.StatusMessage, "Codes generated")
        );

        Assert.Contains("Put these codes in a safe place", cut.Markup);
        var renderedCodes = cut.FindAll("code.recovery-code");
        Assert.Equal(3, renderedCodes.Count);
        Assert.Equal("ABCD-1234", renderedCodes[0].TextContent);
        Assert.Equal("EFGH-5678", renderedCodes[1].TextContent);
        Assert.Equal("IJKL-9012", renderedCodes[2].TextContent);
    }

    [Fact]
    public void AccessDenied_RendersDeniedMessage()
    {
        var cut = Render<AccessDenied>();

        Assert.Contains("Access denied", cut.Markup);
        Assert.Contains("You do not have access to this resource.", cut.Markup);
    }

    [Fact]
    public void Lockout_RendersLockedOutMessage()
    {
        var cut = Render<Lockout>();

        Assert.Contains("Locked out", cut.Markup);
        Assert.Contains("This account has been locked out, please try again later.", cut.Markup);
    }

    [Fact]
    public void NotFound_RendersNotFoundMessage()
    {
        var cut = Render<NotFound>();

        Assert.Contains("Not Found", cut.Markup);
        Assert.Contains("Sorry, the content you are looking for does not exist.", cut.Markup);
    }

    private sealed class TestAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                new Claim(ClaimTypes.Name, "admin@example.com"),
                new Claim(ClaimTypes.Role, Roles.Admin)
            ], "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    [Fact]
    public async Task Settings_EmailTab_AllowsSelectingEnableSslOptionAndSaves()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        await using (var initDb = new ApplicationDbContext(options))
        {
            await initDb.Database.EnsureCreatedAsync();
        }

        var factory = new TestDbContextFactory(options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32])
            })
            .Build();
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var tenantCtx = new TenantContext();
        var tenantAccessor = new TenantContextAccessor { Current = tenantCtx };
        var crypto = new CryptoService(config);
        var settingsSvc = new SettingsService(factory, memCache, tenantAccessor);
        var auditSvc = new AuditService(factory, NullLogger<AuditService>.Instance);
        var providers = new DatabaseProviderFactory();
        var metadataSvc = new MetadataService(factory, crypto, memCache, settingsSvc, providers);
        var httpServices = new ServiceCollection();
        httpServices.AddHttpClient();
        var httpFactory = httpServices.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var webhookSvc = new WebhookService(factory, httpFactory, NullLogger<WebhookService>.Instance);
        var notificationSvc = new NotificationService(factory, config, settingsSvc, crypto, webhookSvc, NullLogger<NotificationService>.Instance);
        var aiSvc = new AiAssistantService(httpFactory, settingsSvc, crypto, metadataSvc, new SqlGate(), auditSvc, NullLogger<AiAssistantService>.Instance);
        var oidcSvc = new OidcService(httpFactory, settingsSvc, crypto);
        var piiSvc = new PiiDiscoveryService(metadataSvc, factory);

        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton<AuthenticationStateProvider, TestAuthStateProvider>();
        Services.AddSingleton(new ClientInfo());
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<ITenantContext>(tenantCtx);
        Services.AddSingleton<ITenantContextAccessor>(tenantAccessor);
        Services.AddSingleton(crypto);
        Services.AddSingleton(settingsSvc);
        Services.AddSingleton(auditSvc);
        Services.AddSingleton(metadataSvc);
        Services.AddSingleton(webhookSvc);
        Services.AddSingleton(notificationSvc);
        Services.AddSingleton(aiSvc);
        Services.AddSingleton(oidcSvc);
        Services.AddSingleton(piiSvc);

        var cut = Render<Settings>();
        cut.WaitForAssertion(() => Assert.Contains("Email & SMTP", cut.Markup));

        cut.FindAll("button.tab").First(b => b.TextContent.Contains("Email & SMTP")).Click();

        var sslSelect = cut.FindAll("select")
            .First(s => s.InnerHtml.Contains("Yes (SSL/TLS / STARTTLS)"));
        Assert.Equal("true", sslSelect.GetAttribute("value"));

        sslSelect.Change("false");
        Assert.Equal("false", cut.FindAll("select")
            .First(s => s.InnerHtml.Contains("Yes (SSL/TLS / STARTTLS)"))
            .GetAttribute("value"));

        cut.Find("input[placeholder='e.g. smtp.office365.com, smtp.gmail.com']").Change("smtp.example.com");
        cut.Find("input[placeholder='e.g. Query Gate Notifications']").Change("Query Gate Alerts");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save SMTP configuration")).Click();

        await cut.InvokeAsync(async () =>
        {
            var saved = await settingsSvc.GetSmtpConfigAsync(crypto, config);
            Assert.True(saved.IsConfigured);
            Assert.False(saved.EnableSsl);
            Assert.Equal("smtp.example.com", saved.Host);
            Assert.Equal("Query Gate Alerts", saved.FromName);
        });
    }
}
