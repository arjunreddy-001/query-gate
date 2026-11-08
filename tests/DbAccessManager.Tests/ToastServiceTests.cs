using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class ToastServiceTests
{
    [Fact]
    public void Show_AddsToast_And_Triggers_OnChange()
    {
        var service = new ToastService();
        var changeFired = false;
        service.OnChange += () => changeFired = true;

        service.Show("Grant added successfully.");

        Assert.True(changeFired);
        Assert.Single(service.Toasts);
        var toast = service.Toasts[0];
        Assert.Equal("Grant added successfully.", toast.Message);
        Assert.False(toast.IsError);
        Assert.False(toast.IsWarn);
        Assert.Null(toast.Title);
    }

    [Fact]
    public void Show_Error_And_Warning_Flags_Are_Preserved()
    {
        var service = new ToastService();
        service.Show("Something broke", isError: true, title: "Custom Error");
        service.Show("Check this out", isWarn: true, title: "Notice");

        Assert.Equal(2, service.Toasts.Count);
        Assert.True(service.Toasts[0].IsError);
        Assert.Equal("Custom Error", service.Toasts[0].Title);

        Assert.True(service.Toasts[1].IsWarn);
        Assert.Equal("Notice", service.Toasts[1].Title);
    }

    [Fact]
    public void Show_Ignores_Empty_Or_Whitespace_Messages()
    {
        var service = new ToastService();
        var changeFired = false;
        service.OnChange += () => changeFired = true;

        service.Show("");
        service.Show("   ");

        Assert.False(changeFired);
        Assert.Empty(service.Toasts);
    }

    [Fact]
    public void Dismiss_RemovesToast_And_Triggers_OnChange()
    {
        var service = new ToastService();
        service.Show("Toast 1");
        service.Show("Toast 2");

        var toastId = service.Toasts[0].Id;
        var changeFired = false;
        service.OnChange += () => changeFired = true;

        service.Dismiss(toastId);

        Assert.True(changeFired);
        Assert.Single(service.Toasts);
        Assert.Equal("Toast 2", service.Toasts[0].Message);
    }

    [Fact]
    public void Clear_Removes_All_Toasts()
    {
        var service = new ToastService();
        service.Show("Toast 1");
        service.Show("Toast 2");
        service.Show("Toast 3");

        Assert.Equal(3, service.Toasts.Count);

        service.Clear();

        Assert.Empty(service.Toasts);
    }

    [Fact]
    public async Task AutoDismiss_Removes_Toast_After_Duration()
    {
        var service = new ToastService();
        service.Show("Short lived toast", durationMs: 50);

        Assert.Single(service.Toasts);

        await Task.Delay(150);

        Assert.Empty(service.Toasts);
    }
}
