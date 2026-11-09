using System.Security.Claims;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace DbAccessManager.Services;

public static class ApiEndpoints
{
    private const string RefreshCookie = "dbam_refresh";

    public sealed record LoginRequest(string Email, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record ExecuteRequest(Guid DatabaseId, string Sql, int? TimeoutSeconds, bool DryRun, bool ConfirmUnfilteredWrites);

    private static Actor ActorOf(HttpContext http) => new(
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", http.User.Identity?.Name ?? "",
        http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        var auth = api.MapGroup("/auth").RequireRateLimiting("auth");

        auth.MapPost("/login", async (LoginRequest body, HttpContext http, UserManager<ApplicationUser> users, TokenService tokens, AuditService audit) =>
        {
            var actor = new Actor("", body.Email ?? "", http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
            var user = string.IsNullOrWhiteSpace(body.Email) ? null : await users.FindByEmailAsync(body.Email);
            if (user is null || !user.CanSignIn(DateTimeOffset.UtcNow) || await users.IsLockedOutAsync(user))
            {
                await audit.TryWriteAsync(actor, "Auth", "ApiLogin", "Failed");
                return Results.Problem("Invalid credentials.", statusCode: 401);
            }
            if (!await users.CheckPasswordAsync(user, body.Password ?? ""))
            {
                await users.AccessFailedAsync(user);
                await audit.TryWriteAsync(actor, "Auth", "ApiLogin", "Failed");
                return Results.Problem("Invalid credentials.", statusCode: 401);
            }
            await users.ResetAccessFailedCountAsync(user);
            var roles = await users.GetRolesAsync(user);
            var refresh = await tokens.CreateRefreshTokenAsync(user.Id);
            SetRefreshCookie(http, refresh);
            await audit.TryWriteAsync(actor with { UserId = user.Id, UserName = user.Email ?? "" }, "Auth", "ApiLogin", "Success");
            return Results.Ok(new { accessToken = tokens.CreateAccessToken(user, roles), expiresInSeconds = (int)TokenService.AccessLifetime.TotalSeconds, mustChangePassword = user.MustChangePassword });
        }).AllowAnonymous();

        auth.MapPost("/refresh", async (HttpContext http, TokenService tokens, UserManager<ApplicationUser> users) =>
        {
            if (!http.Request.Cookies.TryGetValue(RefreshCookie, out var raw) || string.IsNullOrEmpty(raw)) return Results.Unauthorized();
            var rotated = await tokens.RotateAsync(raw);
            if (rotated is null) { http.Response.Cookies.Delete(RefreshCookie); return Results.Unauthorized(); }
            var user = await users.FindByIdAsync(rotated.Value.UserId);
            if (user is null || !user.CanSignIn(DateTimeOffset.UtcNow)) return Results.Unauthorized();
            SetRefreshCookie(http, rotated.Value.NewRefresh);
            return Results.Ok(new { accessToken = tokens.CreateAccessToken(user, await users.GetRolesAsync(user)), expiresInSeconds = (int)TokenService.AccessLifetime.TotalSeconds });
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext http, TokenService tokens) =>
        {
            if (http.Request.Cookies.TryGetValue(RefreshCookie, out var raw) && !string.IsNullOrEmpty(raw)) await tokens.RevokeAsync(raw);
            http.Response.Cookies.Delete(RefreshCookie);
            return Results.NoContent();
        }).AllowAnonymous();

        var secured = api.MapGroup("").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });

        secured.MapPost("/auth/change-password", async (ChangePasswordRequest body, HttpContext http, UserManager<ApplicationUser> users, AuditService audit) =>
        {
            var user = await users.FindByIdAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            if (user is null || !user.CanSignIn(DateTimeOffset.UtcNow) || await users.IsLockedOutAsync(user)) return Results.Unauthorized();
            var result = await users.ChangePasswordAsync(user, body.CurrentPassword, body.NewPassword);
            if (!result.Succeeded) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = result.Errors.Select(e => e.Description).ToArray() });
            user.MustChangePassword = false;
            await users.UpdateAsync(user);
            await audit.TryWriteAsync(ActorOf(http), "Auth", "ChangePassword", "Success");
            return Results.NoContent();
        });

        secured.MapGet("/databases", async (HttpContext http, PermissionService permissions) =>
            Results.Ok((await permissions.GetAccessibleDatabasesAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!))
                .Select(d => new { d.Id, d.Name, d.Environment, d.Description })));

        secured.MapGet("/access/effective/{databaseId:guid}", async (Guid databaseId, HttpContext http, PermissionService permissions) =>
            Results.Ok((await permissions.GetEffectiveAsync(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, databaseId))
                .Select(p => new { permission = p.PermissionCode, allowed = p.IsAllowed, source = p.Source.ToString(), roles = p.Roles })));

        secured.MapPost("/query/execute", async (ExecuteRequest body, HttpContext http, ExecutionService execution) =>
        {
            var result = await execution.ExecuteAsync(ActorOf(http), new ExecutionOptions
            {
                SqlText = body.Sql ?? "", DatabaseId = body.DatabaseId, TimeoutSeconds = body.TimeoutSeconds,
                DryRun = body.DryRun, ConfirmUnfilteredWrites = body.ConfirmUnfilteredWrites
            }, http.RequestAborted);
            var status = result.Outcome switch { "Denied" => 403, "AuditFailure" => 503, "NeedsConfirmation" => 409, _ => 200 };
            return Results.Json(new
            {
                result.RunId, result.Outcome, result.Error, result.ErrorLine, result.Denials, result.Messages, result.DurationMs,
                result.RowsAffected, result.RowsReturned, result.UnfilteredWrites,
                resultSets = result.ResultSets.Select(s => new { s.Columns, s.Rows, s.Truncated })
            }, statusCode: status);
        });

        secured.MapPost("/query/{runId:guid}/cancel", (Guid runId, HttpContext http, ExecutionService execution) =>
            execution.Cancel(runId, http.User.FindFirstValue(ClaimTypes.NameIdentifier)!, http.User.IsInRole(Roles.Admin)) ? Results.Accepted() : Results.NotFound());
    }

    private static void SetRefreshCookie(HttpContext http, string value) =>
        http.Response.Cookies.Append(RefreshCookie, value, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/api/v1/auth",
            Expires = DateTimeOffset.UtcNow.Add(TokenService.RefreshLifetime)
        });
}
