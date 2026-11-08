using System.Net.Http.Json;
using System.Text.Json;
using DbAccessManager.Data;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "Slack"; // "Slack", "Teams", "ZohoCliq", "Discord", "Generic"
    public string Url { get; set; } = "";
    public string EventFilter { get; set; } = "All"; // "All", "Requests", "Executions"
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WebhookService(
    IDbContextFactory<ApplicationDbContext> factory,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookService> logger)
{
    private const string SettingPrefix = "Webhook:";

    public static readonly string[] Platforms = ["Slack", "Teams", "ZohoCliq", "Discord", "Generic"];
    public static readonly string[] EventFilters = ["All", "Requests", "Executions"];

    public static string PlatformLabel(string platform) => platform switch
    {
        "Slack" => "Slack",
        "Teams" => "Microsoft Teams",
        "ZohoCliq" => "Zoho Cliq",
        "Discord" => "Discord",
        "Generic" => "Generic Webhook",
        _ => platform
    };

    public static string EventFilterLabel(string filter) => filter switch
    {
        "All" => "All notifications",
        "Requests" => "Access & script requests only",
        "Executions" => "Script executions only",
        _ => filter
    };

    public async Task<List<WebhookSubscription>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var entries = await db.Settings.AsNoTracking()
            .Where(s => s.Key.StartsWith(SettingPrefix))
            .ToListAsync();

        var list = new List<WebhookSubscription>();
        foreach (var entry in entries)
        {
            try
            {
                var sub = JsonSerializer.Deserialize<WebhookSubscription>(entry.Value);
                if (sub is not null) list.Add(sub);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize webhook setting {Key}", entry.Key);
            }
        }
        return list.OrderBy(w => w.CreatedAt).ToList();
    }

    public async Task<WebhookSubscription?> GetAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entry = await db.Settings.FindAsync($"{SettingPrefix}{id}");
        if (entry is null) return null;
        try { return JsonSerializer.Deserialize<WebhookSubscription>(entry.Value); }
        catch { return null; }
    }

    public async Task SaveAsync(WebhookSubscription webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook.Name)) throw new ArgumentException("Webhook name is required.");
        if (string.IsNullOrWhiteSpace(webhook.Url) || !Uri.TryCreate(webhook.Url, UriKind.Absolute, out _))
            throw new ArgumentException("A valid absolute webhook URL is required.");

        var json = JsonSerializer.Serialize(webhook);
        await using var db = await factory.CreateDbContextAsync();
        var key = $"{SettingPrefix}{webhook.Id}";
        var existing = await db.Settings.FindAsync(key);
        if (existing is null)
        {
            db.Settings.Add(new SettingEntry { Key = key, Value = json });
        }
        else
        {
            existing.Value = json;
        }
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var key = $"{SettingPrefix}{id}";
        var existing = await db.Settings.FindAsync(key);
        if (existing is not null)
        {
            db.Settings.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

    public async Task ToggleEnabledAsync(Guid id)
    {
        var sub = await GetAsync(id);
        if (sub is null) return;
        sub.IsEnabled = !sub.IsEnabled;
        await SaveAsync(sub);
    }

    public async Task<OpResult> TestWebhookAsync(WebhookSubscription webhook)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var payload = BuildPayload(webhook.Platform, "Test", "Test Webhook", "This is a test notification from Query Gate to verify your webhook integration.", "/admin/settings");

            var response = await client.PostAsJsonAsync(webhook.Url, payload);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                return new OpResult(true, Value: $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }
            return new OpResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {(body.Length > 200 ? body[..200] : body)}");
        }
        catch (Exception ex)
        {
            return new OpResult(false, $"Failed to deliver test webhook: {ex.Message}");
        }
    }

    public async Task BroadcastAsync(string eventType, string title, string message, string? link = null)
    {
        try
        {
            var webhooks = await ListAsync();
            var active = webhooks.Where(w => w.IsEnabled && MatchesFilter(w.EventFilter, eventType)).ToList();
            if (active.Count == 0) return;

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            foreach (var hook in active)
            {
                try
                {
                    var payload = BuildPayload(hook.Platform, eventType, title, message, link);
                    var response = await client.PostAsJsonAsync(hook.Url, payload);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        logger.LogWarning("Webhook {Name} ({Platform}) returned HTTP {Status}: {Body}", hook.Name, hook.Platform, (int)response.StatusCode, body);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to deliver webhook {Name} to {Url}", hook.Name, hook.Url);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while processing webhook broadcast");
        }
    }

    private static bool MatchesFilter(string filter, string eventType)
    {
        if (string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(filter, "Requests", StringComparison.OrdinalIgnoreCase))
            return string.Equals(eventType, "Requests", StringComparison.OrdinalIgnoreCase) ||
                   eventType.Contains("Request", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "Executions", StringComparison.OrdinalIgnoreCase))
            return string.Equals(eventType, "Executions", StringComparison.OrdinalIgnoreCase) ||
                   eventType.Contains("Execut", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public static object BuildPayload(string platform, string eventType, string title, string message, string? link)
    {
        return platform switch
        {
            "Slack" => BuildSlackPayload(title, message, link),
            "Teams" => BuildTeamsPayload(title, message, link),
            "ZohoCliq" => BuildZohoCliqPayload(title, message, link),
            "Discord" => BuildDiscordPayload(title, message, link),
            _ => BuildGenericPayload(eventType, title, message, link)
        };
    }

    private static object BuildSlackPayload(string title, string message, string? link)
    {
        var textContent = $"*[Query Gate] {title}*\n{message}" + (link is not null ? $"\n<{link}|View details>" : "");
        return new
        {
            text = textContent,
            blocks = new object[]
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = $"[Query Gate] {title}", emoji = true }
                },
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = message + (link is not null ? $"\n<{link}|Open in Query Gate>" : "") }
                }
            }
        };
    }

    private static object BuildTeamsPayload(string title, string message, string? link)
    {
        var actions = link is null ? (object[])[] :
        [
            new { type = "Action.OpenUrl", title = "Open in Query Gate", url = link }
        ];

        return new
        {
            type = "message",
            summary = $"[Query Gate] {title}",
            attachments = new object[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", text = "Query Gate", weight = "Bolder", size = "Small", color = "Accent" },
                            new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true },
                            new { type = "TextBlock", text = message, wrap = true }
                        },
                        actions
                    }
                }
            }
        };
    }

    private static object BuildZohoCliqPayload(string title, string message, string? link)
    {
        var text = $"*[Query Gate] {title}*\n{message}" + (link is not null ? $"\n[Open in Query Gate]({link})" : "");
        return new
        {
            text,
            card = new
            {
                title = $"Query Gate: {title}",
                theme = "modern-inline"
            }
        };
    }

    private static object BuildDiscordPayload(string title, string message, string? link)
    {
        var content = $"**[Query Gate] {title}**\n{message}" + (link is not null ? $"\n{link}" : "");
        return new { content };
    }

    private static object BuildGenericPayload(string eventType, string title, string message, string? link)
    {
        return new
        {
            source = "QueryGate",
            @event = eventType,
            title,
            message,
            link,
            timestamp = DateTimeOffset.UtcNow
        };
    }
}
