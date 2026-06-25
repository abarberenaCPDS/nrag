using DotnetRag.Blazor.Models;

namespace DotnetRag.Blazor.State;

public sealed class ChatState
{
    public List<ChatMessage> Messages { get; } = [];
    public bool IsStreaming { get; private set; }
    public string? AbortedMessageId { get; private set; }

    public event Action? OnChange;

    public ChatMessage AddUserMessage(string content, List<AttachedImage>? images = null)
    {
        var msg = new ChatMessage(Guid.NewGuid().ToString(), "user", content)
        {
            Images = images ?? []
        };
        Messages.Add(msg);
        OnChange?.Invoke();
        return msg;
    }

    public ChatMessage AddAssistantMessage()
    {
        var msg = new ChatMessage(Guid.NewGuid().ToString(), "assistant", "") { IsStreaming = true };
        Messages.Add(msg);
        IsStreaming = true;
        OnChange?.Invoke();
        return msg;
    }

    public void AppendToken(string messageId, string token)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        msg.Content += token;
        OnChange?.Invoke();
    }

    public void StartReasoningStage(string messageId, string stage, string? label)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        foreach (var s in msg.ReasoningSteps.Where(s => s.Status == "running"))
            s.Status = "done";
        msg.ReasoningSteps.Add(new ReasoningStep { Stage = stage, Label = label });
        OnChange?.Invoke();
    }

    public void EndReasoningStage(string messageId, string? summary)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        var last = msg.ReasoningSteps.LastOrDefault(s => s.Status == "running");
        if (last is not null) { last.Summary = summary; last.Status = "done"; }
        OnChange?.Invoke();
    }

    public void AddReasoningContent(string messageId, string? stage, string content, bool isOutput)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        var step = msg.ReasoningSteps.LastOrDefault(s => s.Status == "running");
        if (step is null || (stage is not null && step.Stage != stage && step.Stage != ""))
        {
            step = new ReasoningStep { Stage = stage ?? "unknown" };
            msg.ReasoningSteps.Add(step);
        }
        if (isOutput) step.Output += content;
        else step.Reasoning += content;
        OnChange?.Invoke();
    }

    public void CloseAllReasoningSteps(string messageId, bool isError = false)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        foreach (var s in msg.ReasoningSteps.Where(s => s.Status == "running"))
            s.Status = isError ? "error" : "done";
    }

    public void SetCitations(string messageId, CitationsWrapper citations)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        msg.Citations = citations.Results;
        OnChange?.Invoke();
    }

    public void FinalizeMessage(string messageId, bool isError = false)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        // Close any still-running reasoning steps
        foreach (var s in msg.ReasoningSteps.Where(s => s.Status == "running"))
            s.Status = isError ? "error" : "done";
        msg.IsStreaming = false;
        msg.IsError = isError;
        IsStreaming = false;
        OnChange?.Invoke();
    }

    public void SetError(string messageId, string errorText)
    {
        var msg = Messages.FirstOrDefault(m => m.Id == messageId);
        if (msg is null) return;
        msg.Content = errorText;
        msg.IsError = true;
        msg.IsStreaming = false;
        IsStreaming = false;
        OnChange?.Invoke();
    }

    public void Clear()
    {
        Messages.Clear();
        IsStreaming = false;
        OnChange?.Invoke();
    }
}
