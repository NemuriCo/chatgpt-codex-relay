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

public enum RelayPayloadKind
{
    TextMarkdown
}

/// <summary>
/// Metadata for a task/result body stored outside state.json.
/// </summary>
public sealed class RelayPayload
{
    public RelayPayloadKind Kind { get; set; } = RelayPayloadKind.TextMarkdown;

    public string Path { get; set; } = string.Empty;

    public long Length { get; set; }

    public string Sha256 { get; set; } = string.Empty;
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

    public RelayPayload? Payload { get; set; }

    public RelayPayload? ResultPayload { get; set; }

    public string? UserNote { get; set; }

    public string? ResultNote { get; set; }

    public string? CodexTurnId { get; set; }

    public string? CodexError { get; set; }

    public string SourceTabKey { get; set; } = string.Empty;

    public string? SourceTabId { get; set; }

    public string? SourceChatGPTUrl { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public RelayTaskStatus Status { get; set; } = RelayTaskStatus.Captured;

    public RelayCommandDeliveryStatus DeliveryStatus { get; set; } = RelayCommandDeliveryStatus.None;

    public string? DeliveryErrorCode { get; set; }
}
