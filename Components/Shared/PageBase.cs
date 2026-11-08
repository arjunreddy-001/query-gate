using System.Security.Claims;
using DbAccessManager.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace DbAccessManager.Components.Shared;

public abstract class PageBase : ComponentBase
{
    [Inject] protected AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] protected ClientInfo Client { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected ToastService Toast { get; set; } = default!;

    protected string UserId { get; private set; } = "";
    protected string UserName { get; private set; } = "";
    protected bool IsAdmin { get; private set; }
    protected Actor Me => new(UserId, UserName, Client.Ip, Client.UserAgent);

    protected string? Flash { get; set; }
    protected bool FlashIsError { get; set; }

    protected override async Task OnInitializedAsync()
    {
        var user = (await Auth.GetAuthenticationStateAsync()).User;
        UserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        UserName = user.Identity?.Name ?? "";
        IsAdmin = user.IsInRole(Security.Roles.Admin);
        await OnLoadAsync();
    }

    protected virtual Task OnLoadAsync() => Task.CompletedTask;

    protected void Say(string message, bool error = false)
    {
        Flash = message;
        FlashIsError = error;
        Toast?.Show(message, isError: error);
    }

    protected void Say(OpResult result, string success)
    {
        Flash = result.Ok ? success : result.Error;
        FlashIsError = !result.Ok;
        if (result.Ok)
        {
            Toast?.Show(success, isError: false);
        }
        else
        {
            Toast?.Show(result.Error ?? "Operation failed.", isError: true);
        }
    }

    protected static string EnvClass(string env) => env switch
    {
        "Development" => "env-dev",
        "Production" => "env-prod",
        _ => "env-qa"
    };

    protected static string OutcomeClass(string outcome) => outcome switch
    {
        "Success" or "Approved" or "Executed" => "ok",
        "Running" or "Pending" => "info",
        "Denied" or "Failed" or "Error" or "Rejected" or "Timeout" or "AuditFailure" => "bad",
        _ => "warn"
    };

    protected static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    protected static string Local(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    protected async Task DownloadFileAsync(string filename, string content, string mimeType = "text/csv;charset=utf-8")
    {
        await JS.InvokeVoidAsync("dbam.download", filename, content, mimeType);
    }

    protected async Task DownloadBinaryAsync(string filename, byte[] bytes, string mimeType = "application/octet-stream")
    {
        var base64 = Convert.ToBase64String(bytes);
        await JS.InvokeVoidAsync("dbam.downloadBase64", filename, base64, mimeType);
    }
}
