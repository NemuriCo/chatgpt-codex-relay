using BlueRelay.Models;
using BlueRelay.Services.Codex;

namespace BlueRelay.Services.Bridges;

public interface ICodexBridge
{
    CodexBridgeStatus Status { get; }

    string? Version { get; }

    event EventHandler<CodexProgressUpdate>? ProgressChanged;

    event EventHandler<CodexApprovalRequest>? ApprovalRequested;

    event EventHandler<CodexThreadUpdate>? ThreadChanged;

    event EventHandler? StatusChanged;

    Task<CodexTurnResult> SubmitTaskAsync(CodexTaskRequest request, CancellationToken cancellationToken = default);

    Task<bool> InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken = default);

    Task RespondToApprovalAsync(
        string requestId,
        string method,
        bool approved,
        System.Text.Json.JsonElement? parameters = null,
        CancellationToken cancellationToken = default);

    Task ResetThreadAsync(Workstream workstream, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
