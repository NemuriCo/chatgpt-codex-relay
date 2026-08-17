using System.Collections.Concurrent;
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
    bool Cancelled = false,
    string? ErrorCode = null);

public sealed class CodexThreadConflictException : Exception
{
    public CodexThreadConflictException(string threadId, string message)
        : base(message)
    {
        ThreadId = threadId;
    }

    public string ThreadId { get; }
}

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
    private readonly ICodexExecutableLocator _locator;
    private readonly CodexDiagnosticBuffer _diagnostics = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _attachmentGate = new();
    private readonly object _detachGate = new();
    private readonly HashSet<string> _attachedThreadIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _workstreamGates = new();
    private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new(StringComparer.Ordinal);
    private ICodexAppServerProcess? _process;
    private CodexProtocolClient? _protocol;
    private CodexBridgeStatus _status = CodexBridgeStatus.Disconnected;
    private string? _version;
    private int _disconnectHandled;
    private int _stopping;
    private long _connectionGeneration;
    private Task _detachTask = Task.CompletedTask;

    public CodexAppServerBridge(
        ApplicationState state,
        ICodexExecutableLocator? locator = null,
        Func<string, ICodexAppServerProcess>? processFactory = null)
    {
        _state = state;
        _locator = locator ?? new CodexExecutableLocator();
        ProcessFactory = processFactory ?? (path => new CodexAppServerProcess(path));
    }

    internal Func<string, ICodexAppServerProcess> ProcessFactory { get; }

    public CodexBridgeStatus Status => _status;

    public string? Version => _version;

    public string? ErrorMessage => _diagnostics.Snapshot().ErrorMessage;

    public CodexDiagnosticSnapshot Diagnostics => _diagnostics.Snapshot();

    public event EventHandler<CodexProgressUpdate>? ProgressChanged;

    public event EventHandler<CodexApprovalRequest>? ApprovalRequested;

    public event EventHandler<CodexThreadUpdate>? ThreadChanged;

    public event EventHandler? StatusChanged;

    public event EventHandler? DiagnosticsChanged;

    public async Task<CodexTurnResult> SubmitTaskAsync(
        CodexTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var workingDirectory = ResolveWorkingDirectory(request.Project.LocalPath);
        if (!Directory.Exists(workingDirectory))
        {
            return Failure($"Project directory does not exist: {workingDirectory}", errorCode: "codex_project_directory_missing");
        }

        var gate = _workstreamGates.GetOrAdd(request.Workstream.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexProtocolClient protocol;
            string threadId;
            try
            {
                protocol = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
                SetError(null);
                threadId = await EnsureThreadAsync(protocol, request, workingDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (CodexThreadConflictException exception)
            {
                var error = exception.Message;
                SetError(error);
                return Failure(error, exception.ThreadId, errorCode: "codex_thread_conflict");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var error = FormatFailure(exception);
                SetError(error);
                var hasExistingThread = !string.IsNullOrWhiteSpace(request.Workstream.CodexThreadId);
                var errorCode = hasExistingThread
                    ? ClassifyThreadResumeFailure(exception)
                    : "codex_thread_start_failed";
                return Failure(error, request.Workstream.CodexThreadId, errorCode: errorCode);
            }

            var active = new ActiveTurn(request.Workstream.Id, threadId);
            if (!_activeTurns.TryAdd(threadId, active))
            {
                return Failure("This Workstream already has a Codex turn running.", threadId: threadId);
            }

            SetStatus(CodexBridgeStatus.Running);
            try
            {
                SetStage("turn_start");
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
                SetStage("running");

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
                var error = FormatFailure(exception);
                SetError(error);
                active.Completion.TrySetResult(Failure(error, threadId, active.TurnId, "codex_turn_failed"));
                return await active.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                _activeTurns.TryRemove(threadId, out _);
                if (_status != CodexBridgeStatus.Error)
                {
                    SetStatus(_activeTurns.IsEmpty ? CodexBridgeStatus.Connected : CodexBridgeStatus.Running);
                }
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
            SetError(FormatFailure(exception));
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
            "item/tool/requestUserInput" => new { answers = new Dictionary<string, object>() },
            _ => new { }
        };
        SetStatus(_activeTurns.IsEmpty ? CodexBridgeStatus.Connected : CodexBridgeStatus.Running);
        return protocol.RespondAsync(requestId, response, cancellationToken);
    }

    public Task ResetThreadAsync(Workstream workstream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousThreadId = workstream.CodexThreadId;
        workstream.CodexThreadId = null;
        workstream.CodexSessionId = null;
        workstream.CodexProgress = null;
        workstream.CodexError = null;
        workstream.CodexErrorCode = null;
        if (!string.IsNullOrWhiteSpace(previousThreadId))
        {
            lock (_attachmentGate)
            {
                _attachedThreadIds.Remove(previousThreadId);
            }

            UpdateAttachedThreadDiagnostics();
            _diagnostics.Add(
                $"thread binding reset workstream={workstream.Id:N} oldThread={ShortIdentity(previousThreadId)} generation={_connectionGeneration}");
            RaiseDiagnosticsChanged();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _stopping, 1);
        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WaitForPendingDetachAsync().ConfigureAwait(false);
            var protocol = Interlocked.Exchange(ref _protocol, null);
            if (protocol is not null)
            {
                await protocol.DisposeAsync().ConfigureAwait(false);
            }

            var process = Interlocked.Exchange(ref _process, null);
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
            lock (_attachmentGate)
            {
                _attachedThreadIds.Clear();
            }

            UpdateAttachedThreadDiagnostics();
            SetStage("stopped");
            SetStatus(CodexBridgeStatus.Disconnected);
        }
        finally
        {
            _startGate.Release();
            Interlocked.Exchange(ref _stopping, 0);
        }
    }

    private async Task<CodexProtocolClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_protocol is { } existing && _status is
            CodexBridgeStatus.Connected or
            CodexBridgeStatus.Running or
            CodexBridgeStatus.WaitingForApproval)
        {
            return existing;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WaitForPendingDetachAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _stopping) != 0)
            {
                throw new InvalidOperationException("Codex App Server is stopping.");
            }

            if (_protocol is { } started && _status is
                CodexBridgeStatus.Connected or
                CodexBridgeStatus.Running or
                CodexBridgeStatus.WaitingForApproval)
            {
                return started;
            }

            SetStatus(CodexBridgeStatus.Connecting);
            SetStage("process_start");
            SetError(null);
            Interlocked.Exchange(ref _disconnectHandled, 0);
            var executable = await _locator.LocateAsync(_state.CodexExecutablePath, cancellationToken).ConfigureAwait(false);
            if (!executable.Found)
            {
                var error = $"Codex 未安装或未找到：{executable.Error}";
                SetError(error);
                SetStatus(CodexBridgeStatus.Error);
                throw new FileNotFoundException(error);
            }

            _version = executable.Version?.Trim();
            _diagnostics.SetExecutable(executable.Path, _version);
            RaiseDiagnosticsChanged();

            var process = ProcessFactory(executable.Path!);
            CodexProtocolClient? protocol = null;
            try
            {
                process.DiagnosticOutput += Process_DiagnosticOutput;
                process.Exited += Process_Exited;
                _process = process;
                await process.StartAsync(cancellationToken).ConfigureAwait(false);
                if (process.Output is null || process.Input is null)
                {
                    throw new InvalidOperationException("The Codex App Server process did not expose stdio after starting.");
                }

                var generation = Interlocked.Increment(ref _connectionGeneration);
                lock (_attachmentGate)
                {
                    _attachedThreadIds.Clear();
                }
                UpdateAttachedThreadDiagnostics();
                _diagnostics.SetGeneration(generation);
                _diagnostics.Add($"process generation started: {generation}");
                _diagnostics.SetProcess(process.ProcessId);
                RaiseDiagnosticsChanged();

                protocol = new CodexProtocolClient(process.Output, process.Input);
                protocol.NotificationReceived += Protocol_NotificationReceived;
                protocol.ServerRequestReceived += Protocol_ServerRequestReceived;
                protocol.Disconnected += Protocol_Disconnected;
                protocol.Diagnostic += Protocol_Diagnostic;
                _protocol = protocol;
                protocol.Start();

                SetStage("initialize");
                var initialize = await protocol.RequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new { name = "bluerelay", title = "BlueRelay", version = "0.1.0" },
                        capabilities = new { experimentalApi = false, requestAttestation = false }
                    },
                    cancellationToken).ConfigureAwait(false);
                var serverUserAgent = ReadString(initialize, "userAgent");
                if (!string.IsNullOrWhiteSpace(serverUserAgent))
                {
                    _diagnostics.Add($"initialize userAgent: {serverUserAgent}");
                    RaiseDiagnosticsChanged();
                }

                SetStage("initialized");
                await protocol.NotifyAsync("initialized", null, cancellationToken).ConfigureAwait(false);
                SetStatus(CodexBridgeStatus.Connected);
                return protocol;
            }
            catch (Exception exception)
            {
                var error = FormatFailure(exception);
                SetError(error);
                var ownedProtocol = protocol is not null &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _protocol, null, protocol), protocol)
                    ? protocol
                    : null;
                var ownedProcess = ReferenceEquals(Interlocked.CompareExchange(ref _process, null, process), process)
                    ? process
                    : null;
                await DetachConnectionAsync(ownedProtocol, ownedProcess).ConfigureAwait(false);
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
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Workstream.CodexThreadId))
        {
            var existingThreadId = request.Workstream.CodexThreadId;
            var owner = _state.Projects
                .SelectMany(project => project.Workstreams)
                .FirstOrDefault(workstream =>
                    workstream.Id != request.Workstream.Id &&
                    string.Equals(workstream.CodexThreadId, existingThreadId, StringComparison.Ordinal));
            if (owner is not null)
            {
                throw new CodexThreadConflictException(
                    existingThreadId,
                    "This Codex session is already bound to another Workstream.");
            }

            lock (_attachmentGate)
            {
                if (_attachedThreadIds.Contains(existingThreadId))
                {
                    SetStage("thread_attached");
                    AddRoutingDiagnostic(request.Workstream, existingThreadId, "thread_attached");
                    return existingThreadId;
                }
            }

            SetStage("thread_resume");
            try
            {
                var resumeResponse = await protocol.RequestAsync(
                    "thread/resume",
                    new { threadId = existingThreadId, cwd = workingDirectory },
                    cancellationToken).ConfigureAwait(false);
                var resumedThreadId = ReadString(resumeResponse, "thread", "id");
                if (!string.IsNullOrWhiteSpace(resumedThreadId) &&
                    !string.Equals(resumedThreadId, existingThreadId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Codex App Server returned thread {resumedThreadId} while resuming {existingThreadId}.");
                }
            }
            catch (CodexProtocolException exception) when (IsActiveWriterConflict(exception))
            {
                AddActiveWriterDiagnostic(existingThreadId);
                throw new CodexThreadConflictException(
                    existingThreadId,
                    "Codex session is being used by another process. BlueRelay could not obtain its writer ownership.");
            }

            lock (_attachmentGate)
            {
                _attachedThreadIds.Add(existingThreadId);
            }
            UpdateAttachedThreadDiagnostics();
            AddRoutingDiagnostic(request.Workstream, existingThreadId, "thread_resume");
            return existingThreadId;
        }

        SetStage("thread_start");
        var response = await protocol.RequestAsync(
            "thread/start",
            new
            {
                cwd = workingDirectory,
                approvalPolicy = "on-request",
                approvalsReviewer = "user",
                sandbox = "workspace-write"
            },
            cancellationToken).ConfigureAwait(false);
        var threadId = ReadString(response, "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidDataException("Codex App Server did not return a thread id.");
        }

        request.Workstream.CodexThreadId = threadId;
        request.Workstream.CodexSessionId = threadId;
        lock (_attachmentGate)
        {
            _attachedThreadIds.Add(threadId);
        }
        UpdateAttachedThreadDiagnostics();
        AddRoutingDiagnostic(request.Workstream, threadId, "thread_start");
        try
        {
            ThreadChanged?.Invoke(this, new CodexThreadUpdate(request.Workstream.Id, threadId));
        }
        catch (Exception exception)
        {
            Protocol_Diagnostic(this, $"thread_changed_handler_error: {exception.GetType().Name}: {exception.Message}");
        }

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
            case "item/agentMessage/delta":
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
            case "item/commandExecution/outputDelta":
                if (active is not null)
                {
                    PublishProgress(active, "执行命令", "Codex 正在执行项目命令。");
                }

                break;
            case "item/fileChange/outputDelta":
            case "item/fileChange/patchUpdated":
                if (active is not null)
                {
                    PublishProgress(active, "修改文件", "Codex 正在修改项目文件。");
                }

                break;
            case "error":
                var serverError = FormatServerError(parameters);
                if (!string.IsNullOrWhiteSpace(serverError))
                {
                    _diagnostics.Add($"server error: {serverError}");
                    RaiseDiagnosticsChanged();
                    if (active is not null)
                    {
                        active.LastServerError = serverError;
                        PublishProgress(active, "连接重试", serverError);
                        if (ReadBoolean(parameters, "willRetry") == false)
                        {
                            SetError(serverError);
                            active.Completion.TrySetResult(
                                Failure(serverError, active.ThreadId, active.TurnId, "codex_turn_failed"));
                        }
                    }
                }

                break;
            case "warning":
                var warning = ReadString(parameters, "message");
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    _diagnostics.Add($"server warning: {warning}");
                    RaiseDiagnosticsChanged();
                }

                break;
            case "turn/completed":
                if (active is not null)
                {
                    var status = ReadString(parameters, "turn", "status") ?? "failed";
                    var result = active.Result.ToString().Trim();
                    var error = FormatTurnError(parameters) ?? active.LastServerError;
                    var cancelled = string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase);
                    var failed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
                    if (!failed && !cancelled && string.IsNullOrWhiteSpace(result))
                    {
                        failed = true;
                        error ??= "Codex completed without a final agent message.";
                    }

                    active.Completion.TrySetResult(
                        failed
                            ? Failure(error ?? "Codex turn failed.", active.ThreadId, active.TurnId, "codex_turn_failed")
                            : new CodexTurnResult(true, active.ThreadId, active.TurnId, result, null, false));
                }

                break;
        }
    }

    private void Protocol_ServerRequestReceived(object? sender, CodexServerRequest request)
    {
        SetStatus(CodexBridgeStatus.WaitingForApproval);
        var handler = ApprovalRequested;
        if (handler is null)
        {
            _ = RespondToApprovalSafelyAsync(request, approved: false);
            return;
        }

        try
        {
            handler.Invoke(this, new CodexApprovalRequest(request.RequestId, request.Method, request.Params));
        }
        catch (Exception exception)
        {
            Protocol_Diagnostic(this, $"approval_handler_error: {exception.GetType().Name}: {exception.Message}");
            _ = RespondToApprovalSafelyAsync(request, approved: false);
        }
    }

    private async Task RespondToApprovalSafelyAsync(CodexServerRequest request, bool approved)
    {
        try
        {
            await RespondToApprovalAsync(request.RequestId, request.Method, approved, request.Params).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetError(FormatFailure(exception));
        }
    }

    private void Protocol_Disconnected(object? sender, Exception exception)
    {
        HandleDisconnect(exception, sender as CodexProtocolClient);
    }

    private void Process_Exited(object? sender, CodexProcessExit exit)
    {
        _diagnostics.SetExitCode(exit.ExitCode);
        RaiseDiagnosticsChanged();
        HandleDisconnect(
            new EndOfStreamException($"Codex App Server exited with code {exit.ExitCode?.ToString() ?? "unknown"}."),
            sender as ICodexAppServerProcess);
    }

    private void HandleDisconnect(Exception exception, object? source)
    {
        if (Interlocked.Exchange(ref _disconnectHandled, 1) != 0 || Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        SetStage("disconnect");
        var error = BuildDisconnectError(exception);
        SetError(error);
        foreach (var active in _activeTurns.Values)
        {
            active.Completion.TrySetResult(Failure(error, active.ThreadId, active.TurnId, "codex_app_server_disconnected"));
        }

        var protocol = Interlocked.Exchange(ref _protocol, null);
        var process = Interlocked.Exchange(ref _process, null);
        lock (_attachmentGate)
        {
            _attachedThreadIds.Clear();
        }
        UpdateAttachedThreadDiagnostics();
        SetStatus(CodexBridgeStatus.Error);
        QueueDetachConnection(
            ReferenceEquals(source, protocol) ? null : protocol,
            process);
    }

    private async Task DetachConnectionAsync(
        CodexProtocolClient? protocol,
        ICodexAppServerProcess? process)
    {
        if (protocol is not null)
        {
            await protocol.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void QueueDetachConnection(
        CodexProtocolClient? protocol,
        ICodexAppServerProcess? process)
    {
        if (protocol is null && process is null)
        {
            return;
        }

        lock (_detachGate)
        {
            _detachTask = DetachAfterPreviousAsync(_detachTask, protocol, process);
        }
    }

    private async Task WaitForPendingDetachAsync()
    {
        Task detachTask;
        lock (_detachGate)
        {
            detachTask = _detachTask;
        }

        await detachTask.ConfigureAwait(false);
    }

    private async Task DetachAfterPreviousAsync(
        Task previous,
        CodexProtocolClient? protocol,
        ICodexAppServerProcess? process)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Protocol_Diagnostic(this, $"previous_detach_error: {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            await DetachConnectionAsync(protocol, process).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Protocol_Diagnostic(this, $"detach_error: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void Process_DiagnosticOutput(object? sender, string message)
    {
        _diagnostics.Add($"stderr: {message}");
        RaiseDiagnosticsChanged();
    }

    private void Protocol_Diagnostic(object? sender, string message)
    {
        _diagnostics.Add($"protocol: {message}");
        RaiseDiagnosticsChanged();
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

        try
        {
            ProgressChanged?.Invoke(
                this,
                new CodexProgressUpdate(active.WorkstreamId, active.ThreadId, active.TurnId, stage, detail));
        }
        catch (Exception exception)
        {
            Protocol_Diagnostic(this, $"progress_handler_error: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void SetStatus(CodexBridgeStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Protocol_Diagnostic(this, $"status_handler_error: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void SetStage(string stage)
    {
        _diagnostics.SetStage(stage);
        RaiseDiagnosticsChanged();
    }

    private void SetError(string? message)
    {
        _diagnostics.SetError(message);
        RaiseDiagnosticsChanged();
    }

    private void RaiseDiagnosticsChanged()
    {
        try
        {
            DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Diagnostics consumers must not affect the bridge.
        }
    }

    private string BuildDisconnectError(Exception exception)
    {
        var snapshot = _diagnostics.Snapshot();
        var stderr = snapshot.RecentMessages
            .LastOrDefault(message => message.StartsWith("stderr:", StringComparison.OrdinalIgnoreCase));
        var reason = string.IsNullOrWhiteSpace(stderr) ? exception.Message : stderr;
        var prefix = snapshot.ExitCode is not null
            ? $"Codex App Server 进程已退出（exit code {snapshot.ExitCode}）"
            : "Codex App Server 连接已断开";
        return $"{prefix}：{reason}";
    }

    private static string FormatFailure(Exception exception)
    {
        if (exception is CodexProtocolException protocolException)
        {
            return $"Codex App Server 协议错误：{protocolException.Message}";
        }

        if (exception.Message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return $"Codex 尚未登录：{exception.Message}";
        }

        return exception.Message;
    }

    private static string? FormatTurnError(JsonElement parameters)
    {
        var message = ReadString(parameters, "turn", "error", "message");
        var details = ReadString(parameters, "turn", "error", "additionalDetails");
        if (string.IsNullOrWhiteSpace(message))
        {
            return details;
        }

        return string.IsNullOrWhiteSpace(details) ? message : $"{message} ({details})";
    }

    private static string? FormatServerError(JsonElement parameters)
    {
        var message = ReadString(parameters, "error", "message");
        var details = ReadString(parameters, "error", "additionalDetails");
        if (string.IsNullOrWhiteSpace(message))
        {
            return details;
        }

        return string.IsNullOrWhiteSpace(details) ? message : $"{message} ({details})";
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

    private static bool? ReadBoolean(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
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

    private void AddRoutingDiagnostic(Workstream workstream, string threadId, string decision)
    {
        var conversation = string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId)
            ? "unknown"
            : ShortIdentity(workstream.ChatGPTConversationId);
        var tab = string.IsNullOrWhiteSpace(workstream.ChatGPTTabId) ? "unknown" : workstream.ChatGPTTabId;
        var snapshot = _diagnostics.Snapshot();
        _diagnostics.Add(
            $"routing decision: {decision} workstream={workstream.Id:N} chatgptConversation={conversation} tab={tab} codexThread={ShortIdentity(threadId)} process={snapshot.ProcessId?.ToString() ?? "unknown"} generation={_connectionGeneration}");
        RaiseDiagnosticsChanged();
    }

    private void AddActiveWriterDiagnostic(string requestedThreadId)
    {
        var snapshot = _diagnostics.Snapshot();
        var attachedThreads = snapshot.AttachedThreadIds is { Count: > 0 }
            ? string.Join(",", snapshot.AttachedThreadIds.Select(ShortIdentity))
            : "none";
        _diagnostics.Add(
            $"active writer conflict requestedThread={ShortIdentity(requestedThreadId)} process={snapshot.ProcessId?.ToString() ?? "unknown"} generation={snapshot.ProcessGeneration?.ToString() ?? "unknown"} attachedThreads={attachedThreads}");
        RaiseDiagnosticsChanged();
    }

    private void UpdateAttachedThreadDiagnostics()
    {
        string[] attachedThreads;
        lock (_attachmentGate)
        {
            attachedThreads = _attachedThreadIds.ToArray();
        }

        _diagnostics.SetAttachedThreadIds(attachedThreads);
        RaiseDiagnosticsChanged();
    }

    private static string ShortIdentity(string value)
    {
        return value.Length <= 14 ? value : $"{value[..8]}…{value[^4..]}";
    }

    private static bool IsActiveWriterConflict(CodexProtocolException exception)
    {
        return exception.Message.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase) ||
               exception.Error.GetRawText().Contains("already has an active writer", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyThreadResumeFailure(Exception exception)
    {
        var message = exception is CodexProtocolException protocolException
            ? $"{protocolException.Message} {protocolException.Error.GetRawText()}"
            : exception.Message;
        return message.Contains("archiv", StringComparison.OrdinalIgnoreCase)
            ? "codex_thread_archived"
            : "codex_thread_resume_failed";
    }

    private static string ResolveWorkingDirectory(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        string normalizedPath;
        try
        {
            normalizedPath = new DirectoryInfo(projectPath.Trim()).FullName;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return projectPath.Trim();
        }

        var current = new DirectoryInfo(normalizedPath);
        while (current is not null)
        {
            var gitMarker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return normalizedPath;
    }

    private static CodexTurnResult Failure(
        string error,
        string? threadId = null,
        string? turnId = null,
        string? errorCode = null)
    {
        return new CodexTurnResult(false, threadId, turnId, null, error, false, errorCode);
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

        public string? LastServerError { get; set; }

        public StringBuilder Result { get; } = new();

        public TaskCompletionSource<CodexTurnResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
