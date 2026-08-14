namespace BlueRelay.Models;

public sealed class Workstream
{
    public const string DefaultName = "Default";

    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string Name { get; set; } = DefaultName;

    public WorkflowState CurrentState { get; set; } = WorkflowState.Idle;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? ChatGPTConversationId { get; set; }

    public string? ChatGPTTabId { get; set; }

    public string? CodexSessionId { get; set; }

    public string? CurrentTaskId { get; set; }
}
