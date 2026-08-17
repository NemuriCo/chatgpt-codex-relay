using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BlueRelay.Models;
using BlueRelay.Services.Bridges;

namespace BlueRelay.Services.Codex;

public enum CodexBridgeStatus
{
    Disconnected,
    Connecting,
    Connected,
    Running,
    WaitingForApproval,
    Error
}

public sealed record CodexTaskRequest(Project Project, Workstream Workstream, RelayTask Task, string Prompt);

public sealed record CodexTurnResult(
    bool Success,
    string? ThreadId,
    string? TurnId,
    string? Result,
    string? Error = null,
    bool Cancelled = false);

public sealed record CodexProgressUpdate(
    Guid WorkstreamId,
    string? ThreadId,
    string? TurnId,
    string Stage,
    string Detail);

public sealed record CodexThreadUpdate(Guid WorkstreamId, string ThreadId);

public sealed record CodexApprovalRequest(
    string RequestId,
    string Method,
    JsonElement Params);

public sealed class CodexAppServerBridge : ICodexBridge
{
    private readonly ApplicationState _state;
    private readonly CodexExecutableLocator _locator;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _workstreamGates = new();
    private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new(StringComparer.Ordinal);
    private CodexAppServerProcess? _process;
    private CodexProtocolClient? _protocol;
    private CodexBridgeStatus _status = CodexBridgeStatus.Disconnected;
    private string? _version;

    public CodexAppServerBridge(ApplicationState state, CodexExecutableLocator? locator = null)
    {
        _state = state;
        _locator = locator ?? new CodexExecutableLocator();
    }

    public CodexBridgeStatus Status => _status;

    public string? Version => _version;

    public event EventHandler<CodexProgressUpdate>? ProgressChanged;

    public event EventHandler<CodexApprovalRequest>? ApprovalRequested;

    public event EventHandler<CodexThreadUpdate>? ThreadChanged;

    public event EventHandler? StatusChanged;

    public async Task<CodexTurnResult> SubmitTaskAsync(
        CodexTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(request.Project.LocalPath))
        {
            return Failure("The project directory no longer exists.");
        }

        var gate = _workstreamGates.GetOrAdd(request.Workstream.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var protocol = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var threadId = await EnsureThreadAsync(protocol, request, cancellationToken).ConfigureAwait(false);
            var active = new ActiveTurn(request.Workstream.Id, threadId);
            if (!_activeTurns.TryAdd(threadId, active))
            {
                return Failure("This Workstream already has a Codex turn running.", threadId: threadId);
            }

            SetStatus(CodexBridgeStatus.Running);
            try
            {
                var response = await protocol.RequestAsync(
                    "turn/start",
                    new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = request.Prompt } }
                    },
                    cancellationToken).ConfigureAwait(false);
                active.TurnId = ReadString(response, "turn", "id") ?? ReadString(response, "turnId");
                PublishProgress(active, "正在提交任务", "Codex 已接收任务。");

                return await active.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!string.IsNullOrWhiteSpace(active.TurnId))
                {
                    await InterruptAsync(threadId, active.TurnId, CancellationToken.None).ConfigureAwait(false);
                }

                return new CodexTurnResult(false, threadId, active.TurnId, null, "Codex turn was cancelled.", true);
            }
            catch (Exception exception)
            {
                active.Completion.TrySetResult(Failure(exception.Message, threadId, active.TurnId));
                return await active.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                _activeTurns.TryRemove(threadId, out _);
                SetStatus(_activeTurns.IsEmpty ? CodexBridgeStatus.Connected : CodexBridgeStatus.Running);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> InterruptAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        var protocol = _protocol;
        if (protocol is null || string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return false;
        }

        try
        {
            await protocol.RequestAsync(
                "turn/interrupt",
                new { threadId, turnId },
                cancellationToken).ConfigureAwait(false);
            PublishProgress(
                _activeTurns.TryGetValue(threadId, out var active) ? active : null,
                "正在取消",
                "已向 Codex 请求取消当前任务。");
            return true;
        }
        catch (Exception exception) when (exception is CodexProtocolException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    public Task RespondToApprovalAsync(
        string requestId,
        string method,
        bool approved,
        JsonElement? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var protocol = _protocol ?? throw new InvalidOperationException("Codex App Server is not connected.");
        object response = method switch
        {
            "item/commandExecution/requestApproval" => new { decision = approved ? "accept" : "decline" },
            "item/fileChange/requestApproval" => new { decision = approved ? "accept" : "decline" },
            "item/permissions/requestApproval" => new
            {
                permissions = approved
                    ? ReadRequestedPermissions(parameters)
                    : new { fileSystem = (object?)null, network = (object?)null },
                scope = "turn"
            },
            _ => new { answers = new Dictionary<string, object>() }
        };
        SetStatus(_activeTurns.IsEmpty ? CodexBridgeStatus.Connected : CodexBridgeStatus.Running);
        return protocol.RespondAsync(requestId, response, cancellationToken);
    }

    private static object ReadRequestedPermissions(JsonElement? parameters)
    {
        if (parameters is { } value &&
            value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("permissions", out var permissions))
        {
            return permissions.Clone();
        }

        return new { fileSystem = (object?)null, network = (object?)null };
    }

    public Task ResetThreadAsync(Workstream workstream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workstream.CodexThreadId = null;
        workstream.CodexSessionId = null;
        workstream.CodexProgress = null;
        workstream.CodexError = null;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var protocol = _protocol;
        _protocol = null;
        if (protocol is not null)
        {
            await protocol.DisposeAsync().ConfigureAwait(false);
        }

        var process = _process;
        _process = null;
        if (process is not null)
        {
            await process.StopAsync(cancellationToken).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var active in _activeTurns.Values)
        {
            active.Completion.TrySetResult(Failure("Codex App Server stopped.", active.ThreadId, active.TurnId));
        }

        _activeTurns.Clear();
        SetStatus(CodexBridgeStatus.Disconnected);
    }

    private async Task<CodexProtocolClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_protocol is { } existing)
        {
            return existing;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_protocol is { } started)
            {
                return started;
            }

            SetStatus(CodexBridgeStatus.Connecting);
            var executable = _locator.Locate(_state.CodexExecutablePath);
            if (!executable.Found)
            {
                SetStatus(CodexBridgeStatus.Error);
                throw new FileNotFoundException(executable.Error);
            }

            _version = await ReadVersionAsync(executable.Path!, cancellationToken).ConfigureAwait(false);
            var process = new CodexAppServerProcess(executable.Path!);
            await process.StartAsync(cancellationToken).ConfigureAwait(false);
            var protocol = new CodexProtocolClient(process.Output!, process.Input!);
            protocol.NotificationReceived += Protocol_NotificationReceived;
            protocol.ServerRequestReceived += Protocol_ServerRequestReceived;
            protocol.Disconnected += Protocol_Disconnected;
            process.Exited += Process_Exited;
            _process = process;
            _protocol = protocol;
            protocol.Start();

            try
            {
                var initialize = await protocol.RequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new { name = "bluerelay", title = "BlueRelay", version = "0.1.0" }
                    },
                    cancellationToken).ConfigureAwait(false);
                _version = ReadString(initialize, "serverInfo", "version") ?? ReadString(initialize, "version");
                await protocol.NotifyAsync("initialized", null, cancellationToken).ConfigureAwait(false);
                SetStatus(CodexBridgeStatus.Connected);
                return protocol;
            }
            catch
            {
                _protocol = null;
                await protocol.DisposeAsync().ConfigureAwait(false);
                await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await process.DisposeAsync().ConfigureAwait(false);
                _process = null;
                SetStatus(CodexBridgeStatus.Error);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<string> EnsureThreadAsync(
        CodexProtocolClient protocol,
        CodexTaskRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Workstream.CodexThreadId))
        {
            await protocol.RequestAsync(
                "thread/resume",
                new { threadId = request.Workstream.CodexThreadId, cwd = request.Project.LocalPath },
                cancellationToken).ConfigureAwait(false);
            return request.Workstream.CodexThreadId;
        }

        var response = await protocol.RequestAsync(
            "thread/start",
            new
            {
                cwd = request.Project.LocalPath,
                approvalPolicy = "on-request",
                approvalsReviewer = "user",
                sandbox = "workspace-write",
                threadSource = "bluerelay"
            },
            cancellationToken).ConfigureAwait(false);
        var threadId = ReadString(response, "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidDataException("Codex App Server did not return a thread id.");
        }

        request.Workstream.CodexThreadId = threadId;
        request.Workstream.CodexSessionId = threadId;
        ThreadChanged?.Invoke(this, new CodexThreadUpdate(request.Workstream.Id, threadId));
        return threadId;
    }

    private void Protocol_NotificationReceived(object? sender, CodexNotification notification)
    {
        var parameters = notification.Params;
        var threadId = ReadString(parameters, "threadId");
        var active = threadId is not null && _activeTurns.TryGetValue(threadId, out var found) ? found : null;
        switch (notification.Method)
        {
            case "turn/started":
                if (active is not null)
                {
                    active.TurnId = ReadString(parameters, "turn", "id") ?? active.TurnId;
                    PublishProgress(active, "Codex 运行中", "Codex 已开始处理任务。");
                }

                break;
            case "agentMessage/delta":
                if (active is not null)
                {
                    active.Result.Append(ReadString(parameters, "delta") ?? string.Empty);
                    PublishProgress(active, "生成回复", "Codex 正在生成结果。");
                }

                break;
            case "item/started":
                if (active is not null)
                {
                    PublishItemProgress(active, ReadString(parameters, "item", "type"));
                }

                break;
            case "item/completed":
                if (active is not null)
                {
                    var itemType = ReadString(parameters, "item", "type");
                    if (string.Equals(itemType, "agentMessage", StringComparison.Ordinal))
                    {
                        var text = ReadString(parameters, "item", "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            active.Result.Clear();
                            active.Result.Append(text);
                        }
                    }

                    PublishItemProgress(active, itemType);
                }

                break;
            case "commandExecution/outputDelta":
                if (active is not null)
                {
                    PublishProgress(active, "执行命令", "Codex 正在执行项目命令。");
                }

                break;
            case "fileChange/outputDelta":
            case "fileChange/patchUpdated":
                if (active is not null)
                {
                    PublishProgress(active, "修改文件", "Codex 正在修改项目文件。");
                }

                break;
            case "turn/completed":
                if (active is not null)
                {
                    var status = ReadString(parameters, "turn", "status") ?? "completed";
                    var result = active.Result.ToString().Trim();
                    var cancelled = string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase);
                    var failed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
                    active.Completion.TrySetResult(
                        failed
                            ? Failure(ReadString(parameters, "turn", "error", "message") ?? "Codex turn failed.", active.ThreadId, active.TurnId)
                            : new CodexTurnResult(!cancelled, active.ThreadId, active.TurnId, result, cancelled ? "Codex turn was interrupted." : null, cancelled));
                }

                break;
        }
    }

    private void Protocol_ServerRequestReceived(object? sender, CodexServerRequest request)
    {
        SetStatus(CodexBridgeStatus.WaitingForApproval);
        ApprovalRequested?.Invoke(this, new CodexApprovalRequest(request.RequestId, request.Method, request.Params));
    }

    private void Protocol_Disconnected(object? sender, Exception exception)
    {
        SetStatus(CodexBridgeStatus.Error);
        foreach (var active in _activeTurns.Values)
        {
            active.Completion.TrySetResult(Failure("Codex App Server disconnected.", active.ThreadId, active.TurnId));
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        Protocol_Disconnected(sender, new EndOfStreamException("Codex App Server exited."));
    }

    private void PublishItemProgress(ActiveTurn active, string? type)
    {
        var (stage, detail) = type switch
        {
            "commandExecution" => ("执行命令", "Codex 正在执行项目命令。"),
            "fileChange" => ("修改文件", "Codex 正在修改项目文件。"),
            "plan" => ("制定计划", "Codex 正在整理执行计划。"),
            "agentMessage" => ("生成回复", "Codex 正在生成结果。"),
            _ => ("Codex 运行中", "Codex 正在处理任务。")
        };
        PublishProgress(active, stage, detail);
    }

    private void PublishProgress(ActiveTurn? active, string stage, string detail)
    {
        if (active is null)
        {
            return;
        }

        ProgressChanged?.Invoke(
            this,
            new CodexProgressUpdate(active.WorkstreamId, active.ThreadId, active.TurnId, stage, detail));
    }

    private void SetStatus(CodexBridgeStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var property in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static CodexTurnResult Failure(string error, string? threadId = null, string? turnId = null)
    {
        return new CodexTurnResult(false, threadId, turnId, null, error);
    }

    private static async Task<string?> ReadVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private sealed class ActiveTurn
    {
        public ActiveTurn(Guid workstreamId, string threadId)
        {
            WorkstreamId = workstreamId;
            ThreadId = threadId;
        }

        public Guid WorkstreamId { get; }

        public string ThreadId { get; }

        public string? TurnId { get; set; }

        public StringBuilder Result { get; } = new();

        public TaskCompletionSource<CodexTurnResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
