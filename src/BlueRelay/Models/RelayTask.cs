namespace BlueRelay.Models;

public enum RelayTaskStatus
{
    Captured,
    CodexRunning,
    ReadyForChatGPT,
    ChatGPTReviewing,
    Completed,
    Error
}

/// <summary>
/// The current task/result pair for a Workstream. Historical tasks can be added later
/// without making Workstream a large browser-runtime object.
/// </summary>
public sealed class RelayTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkstreamId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? Result { get; set; }

    public string SourceTabKey { get; set; } = string.Empty;

    public string? SourceTabId { get; set; }

    public string? SourceChatGPTUrl { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public RelayTaskStatus Status { get; set; } = RelayTaskStatus.Captured;

    public RelayCommandDeliveryStatus DeliveryStatus { get; set; } = RelayCommandDeliveryStatus.None;

    public string? DeliveryErrorCode { get; set; }
}
