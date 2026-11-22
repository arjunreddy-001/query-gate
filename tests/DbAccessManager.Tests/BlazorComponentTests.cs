using Bunit;
using DbAccessManager.Components.Account.Pages;
using DbAccessManager.Components.Account.Shared;
using DbAccessManager.Components.Pages;
using DbAccessManager.Components.Shared;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
}
