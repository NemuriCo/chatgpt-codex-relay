namespace BlueRelay.Models;

public sealed class Project
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public WorkflowState CurrentState { get; set; } = WorkflowState.Idle;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Reserved for future browser-tab and session binding.
    public string? ChatGPTTab { get; set; }

    public string? CodexSessionId { get; set; }

    public string? CurrentTaskId { get; set; }
}
