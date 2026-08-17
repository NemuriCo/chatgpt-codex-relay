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

    public string? BrowserInstallationId { get; set; }

    public string? ChatGPTConversationId { get; set; }

    public string? ChatGPTTabId { get; set; }

    public string? ChatGPTUrl { get; set; }

    public string? ChatGPTTitle { get; set; }

    public string? CodexSessionId { get; set; }

    /// <summary>
    /// Stable Codex App Server thread id owned by this Workstream.
    /// CodexSessionId remains as a legacy compatibility field for older state files.
    /// </summary>
    public string? CodexThreadId { get; set; }

    public string? CodexProgress { get; set; }

    public string? CodexError { get; set; }

    public string? CodexErrorCode { get; set; }

    public string? CurrentTaskId { get; set; }
}
