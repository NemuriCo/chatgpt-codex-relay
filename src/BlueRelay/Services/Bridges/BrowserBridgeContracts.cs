using BlueRelay.Models;

namespace BlueRelay.Services.Bridges;

public sealed record BridgeWorkstreamDto(
    Guid ProjectId,
    string ProjectName,
    Guid WorkstreamId,
    string WorkstreamName,
    WorkflowState CurrentState,
    string? CurrentTaskId,
    string? CurrentTaskPrompt,
    string? CurrentTaskResult,
    string? ChatGPTConversationId,
    string? ChatGPTUrl,
    string? ChatGPTTitle,
    string? CodexThreadId,
    BrowserBindingDto? Binding);

public sealed record BrowserBindingDto(
    string InstallationId,
    string TabId,
    string TabKey,
    Guid? WorkstreamId,
    string ChatGPTUrl,
    string? ChatGPTConversationId,
    string PageTitle,
    DateTimeOffset LastSeenAt,
    bool Connected,
    bool ConversationMismatch = false);

public sealed record BridgeHealthDto(
    string Status,
    bool Paired,
    string BindAddress,
    int Port,
    string? Version = null);

public sealed record RegisterTabRequest(
    string InstallationId,
    string TabId,
    string ChatGPTUrl,
    string? ChatGPTConversationId,
    string PageTitle);

public sealed record BindTabRequest(
    string InstallationId,
    string TabId,
    Guid WorkstreamId,
    bool Rebind = false);

public sealed record CaptureTaskRequest(
    string InstallationId,
    string TabId,
    string Prompt,
    string ChatGPTUrl,
    string? ChatGPTConversationId,
    string PageTitle);

public sealed record TaskActionRequest(string? TaskId = null);

public sealed record SimulatedResultRequest(string Result);

public sealed record HandoffCommand(
    Guid CommandId,
    Guid TaskId,
    Guid WorkstreamId,
    string InstallationId,
    string TabId,
    string ChatGPTUrl,
    string? ChatGPTConversationId,
    string Result,
    RelayCommandDeliveryStatus DeliveryStatus = RelayCommandDeliveryStatus.Queued,
    int AttemptCount = 0,
    DateTimeOffset? LastAttemptAt = null);

public sealed record PairRequest(string PairingCode, string InstallationId);

public sealed record PairResponse(string Token, string InstallationId);

public sealed record PairingCodeInfo(string? Code, DateTimeOffset? ExpiresAt);

public sealed record CommandAcknowledgement(
    bool Success,
    string? Code = null,
    string? Method = null);

public sealed record BridgeOperationResult(bool Success, string ErrorCode = "", string Error = "");

public sealed record BridgeOperationResult<T>(bool Success, T? Value = default, string ErrorCode = "", string Error = "");
