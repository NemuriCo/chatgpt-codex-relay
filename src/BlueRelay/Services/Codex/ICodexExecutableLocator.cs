namespace BlueRelay.Services.Codex;

public interface ICodexExecutableLocator
{
    Task<CodexExecutableInfo> LocateAsync(
        string? configuredPath = null,
        CancellationToken cancellationToken = default);
}
