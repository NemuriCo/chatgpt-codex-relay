using BlueRelay.Models;

namespace BlueRelay.Services.Bridges;

/// <summary>
/// Boundary for future Codex integration. Phase 1 intentionally provides no implementation.
/// </summary>
public interface ICodexBridge
{
    Task<string> SubmitTaskAsync(Project project, string task, CancellationToken cancellationToken = default);
}
