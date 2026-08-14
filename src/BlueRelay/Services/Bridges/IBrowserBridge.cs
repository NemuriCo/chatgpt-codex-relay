using BlueRelay.Models;

namespace BlueRelay.Services.Bridges;

/// <summary>
/// Boundary for a future browser extension or localhost/native-messaging bridge.
/// Phase 1 intentionally provides no implementation.
/// </summary>
public interface IBrowserBridge
{
    Task SendTaskToCodexAsync(Project project, CancellationToken cancellationToken = default);

    Task SendResultToChatGPTAsync(Project project, string result, CancellationToken cancellationToken = default);
}
