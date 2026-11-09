using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using DbAccessManager.Data;

namespace DbAccessManager.Components.Account;

public sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(5),
    };

    public void RedirectTo(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            navigationManager.NavigateTo("dashboard");
            return;
        }

        // Prevent open redirects and invalid URIs.
        if (IsLocalUrl(uri, navigationManager.BaseUri))
        {
            navigationManager.NavigateTo(uri);
            return;
        }

        // Safe fallback for external or malformed URLs
        navigationManager.NavigateTo("dashboard");
    }

    public static bool IsLocalUrl(string? url, string baseUri)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var trimmed = url.Trim();

        // Scheme-relative, backslash or protocol-relative paths are never local
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith("/\\", StringComparison.Ordinal) ||
            trimmed.StartsWith('\\'))
        {
            return false;
        }

        // URLs with schemes or colons (e.g. http:, https:, javascript:)
        if (trimmed.Contains(':'))
        {
            // Only allowed if absolute URI matches app baseUri
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri) &&
                Uri.TryCreate(baseUri, UriKind.Absolute, out var appBaseUri))
            {
                return string.Equals(absoluteUri.Scheme, appBaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(absoluteUri.Authority, appBaseUri.Authority, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        // Local path starting with slash (e.g. /dashboard, /query)
        if (trimmed.StartsWith('/'))
        {
            return true;
        }

        // Relative path without leading slash (e.g. dashboard, query)
        return Uri.TryCreate(trimmed, UriKind.Relative, out _);
    }

    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        RedirectTo(newUri);
    }

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context)
        => RedirectToWithStatus(CurrentPath, message, context);

    public void RedirectToInvalidUser(UserManager<ApplicationUser> userManager, HttpContext context)
        => RedirectToWithStatus("Account/InvalidUser", $"Error: Unable to load user with ID '{userManager.GetUserId(context.User)}'.", context);
}
