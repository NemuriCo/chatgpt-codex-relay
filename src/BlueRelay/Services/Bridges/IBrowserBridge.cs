using BlueRelay.Models;

namespace BlueRelay.Services.Bridges;

/// <summary>
/// Boundary for browser-extension task and result handoff.
/// </summary>
public interface IBrowserBridge
{
    Task SendTaskToCodexAsync(Project project, CancellationToken cancellationToken = default);

    Task SendResultToChatGPTAsync(Project project, string result, CancellationToken cancellationToken = default);
}
