using DotnetRag.Blazor.Models;

namespace DotnetRag.Blazor.State;

public sealed class TaskEntry
{
    public string TaskId { get; init; } = "";
    public string CollectionName { get; init; } = "";
    public string State { get; set; } = "PENDING";
    public int DocumentsCompleted { get; set; }
    public int TotalDocuments { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Dismissed { get; set; }
    public bool IsTerminal => State is "FINISHED" or "FAILED";
}

public sealed class NotificationState
{
    private readonly List<ToastMessage> _toasts = [];
    private readonly List<TaskEntry> _tasks = [];

    public IReadOnlyList<ToastMessage> Toasts => _toasts;
    public IReadOnlyList<TaskEntry> Tasks => _tasks;
    public int UnreadTaskCount => _tasks.Count(t => !t.Dismissed);
    public bool HasHealthWarning { get; private set; }
    public string HealthSummary { get; private set; } = "";

    public event Action? OnChange;

    public void AddToast(string message, ToastSeverity severity = ToastSeverity.Info)
    {
        var toast = new ToastMessage(Guid.NewGuid().ToString(), message, severity);
        _toasts.Add(toast);
        OnChange?.Invoke();

        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            _toasts.Remove(toast);
            OnChange?.Invoke();
        });
    }

    public void RemoveToast(string id)
    {
        _toasts.RemoveAll(t => t.Id == id);
        OnChange?.Invoke();
    }

    public void AddTask(string taskId, string collectionName)
    {
        if (_tasks.Any(t => t.TaskId == taskId)) return;
        _tasks.Add(new TaskEntry { TaskId = taskId, CollectionName = collectionName });
        OnChange?.Invoke();
    }

    public void UpdateTask(string taskId, string state, int completed, int total)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null) return;
        task.State = state;
        task.DocumentsCompleted = completed;
        task.TotalDocuments = total;
        if (task.IsTerminal && task.CompletedAt is null)
            task.CompletedAt = DateTime.Now;
        OnChange?.Invoke();
    }

    public void DismissTask(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task is null) return;
        task.Dismissed = true;
        OnChange?.Invoke();
    }

    public void ClearAllTasks()
    {
        foreach (var t in _tasks) t.Dismissed = true;
        OnChange?.Invoke();
    }

    public void SetHealth(HealthResponse health)
    {
        // Status int values: 0=Healthy, 1=Unhealthy, 2=Skipped, 3=Timeout, 4=Error, 5=Unknown
        var unhealthy = (health.Databases ?? [])
            .Concat(health.ObjectStorage ?? [])
            .Concat(health.Nim ?? [])
            .Where(s => s.Status is 1 or 3 or 4)
            .Select(s => s.Service ?? "unknown")
            .ToList();

        HasHealthWarning = unhealthy.Count > 0;
        HealthSummary = unhealthy.Count > 0
            ? $"Unhealthy: {string.Join(", ", unhealthy)}"
            : "All services healthy";
        OnChange?.Invoke();
    }
}
