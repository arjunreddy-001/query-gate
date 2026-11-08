namespace DbAccessManager.Services;

public sealed class ToastNotification
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Message { get; init; } = "";
    public string? Title { get; init; }
    public bool IsError { get; init; }
    public bool IsWarn { get; init; }
    public int DurationMs { get; init; } = 4000;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}

public sealed class ToastService
{
    public event Action? OnChange;
    private readonly List<ToastNotification> _toasts = [];

    public IReadOnlyList<ToastNotification> Toasts
    {
        get
        {
            lock (_toasts)
            {
                return _toasts.ToList();
            }
        }
    }

    public void Show(string message, bool isError = false, bool isWarn = false, string? title = null, int durationMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var toast = new ToastNotification
        {
            Message = message.Trim(),
            Title = title,
            IsError = isError,
            IsWarn = isWarn,
            DurationMs = durationMs
        };

        lock (_toasts)
        {
            _toasts.Add(toast);
        }

        NotifyChanged();
        _ = AutoDismissAsync(toast);
    }

    public void Dismiss(string id)
    {
        lock (_toasts)
        {
            var item = _toasts.FirstOrDefault(t => t.Id == id);
            if (item is null || !_toasts.Remove(item)) return;
        }

        NotifyChanged();
    }

    public void Clear()
    {
        lock (_toasts)
        {
            _toasts.Clear();
        }

        NotifyChanged();
    }

    private async Task AutoDismissAsync(ToastNotification toast)
    {
        try
        {
            await Task.Delay(toast.DurationMs);
            Dismiss(toast.Id);
        }
        catch
        {
            // Ignore when task is cancelled or circuit disconnects
        }
    }

    private void NotifyChanged()
    {
        try
        {
            OnChange?.Invoke();
        }
        catch
        {
            // Safeguard against disposed UI components
        }
    }
}
