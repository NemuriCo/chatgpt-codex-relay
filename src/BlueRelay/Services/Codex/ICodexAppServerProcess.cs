namespace BlueRelay.Services.Codex;

public interface ICodexAppServerProcess : IAsyncDisposable
{
    TextReader? Output { get; }

    TextWriter? Input { get; }

    int? ProcessId { get; }

    int? ExitCode { get; }

    event EventHandler<CodexProcessExit>? Exited;

    event EventHandler<string>? DiagnosticOutput;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
