namespace BlueRelay.Services.Codex;

public sealed record CodexDiagnosticSnapshot(
    string? ExecutablePath,
    string? Version,
    int? ProcessId,
    int? ExitCode,
    string Stage,
    string? ErrorMessage,
    IReadOnlyList<string> RecentMessages)
{
    public string ToDisplayText()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(ExecutablePath))
        {
            lines.Add($"executable: {ExecutablePath}");
        }

        if (!string.IsNullOrWhiteSpace(Version))
        {
            lines.Add($"version: {Version}");
        }

        if (ProcessId is not null)
        {
            lines.Add($"pid: {ProcessId}");
        }

        if (ExitCode is not null)
        {
            lines.Add($"exit code: {ExitCode}");
        }

        lines.Add($"stage: {Stage}");
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
        {
            lines.Add($"error: {ErrorMessage}");
        }

        lines.AddRange(RecentMessages);
        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class CodexDiagnosticBuffer
{
    private const int MaxMessages = 24;
    private const int MaxMessageLength = 512;
    private readonly object _gate = new();
    private readonly Queue<string> _messages = new();
    private string? _executablePath;
    private string? _version;
    private int? _processId;
    private int? _exitCode;
    private string _stage = "idle";
    private string? _errorMessage;

    public void SetExecutable(string? path, string? version)
    {
        lock (_gate)
        {
            _executablePath = path;
            _version = version;
        }
    }

    public void SetProcess(int? processId)
    {
        lock (_gate)
        {
            _processId = processId;
        }
    }

    public void SetExitCode(int? exitCode)
    {
        lock (_gate)
        {
            _exitCode = exitCode;
        }
    }

    public void SetStage(string stage)
    {
        lock (_gate)
        {
            _stage = Trim(stage);
        }
    }

    public void SetError(string? message)
    {
        lock (_gate)
        {
            _errorMessage = string.IsNullOrWhiteSpace(message) ? null : Trim(message);
        }
    }

    public void Add(string message)
    {
        var trimmed = Trim(message);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        lock (_gate)
        {
            _messages.Enqueue(trimmed);
            while (_messages.Count > MaxMessages)
            {
                _messages.Dequeue();
            }
        }
    }

    public CodexDiagnosticSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new CodexDiagnosticSnapshot(
                _executablePath,
                _version,
                _processId,
                _exitCode,
                _stage,
                _errorMessage,
                _messages.ToArray());
        }
    }

    private static string Trim(string value)
    {
        value ??= string.Empty;
        return value.Length <= MaxMessageLength ? value : value[..MaxMessageLength];
    }
}
