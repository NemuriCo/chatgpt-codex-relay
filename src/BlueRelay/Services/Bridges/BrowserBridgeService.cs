using System.Security.Cryptography;
using BlueRelay.Models;
using BlueRelay.Persistence;
using BlueRelay.Services.Codex;

namespace BlueRelay.Services.Bridges;

/// <summary>
/// Owns browser-tab bindings and current relay tasks. It deliberately sits outside
/// the WPF window so HTTP requests and UI commands use the same state transitions.
/// </summary>
public sealed class BrowserBridgeService
{
    public const int DefaultPort = 48917;
    public const int BindingTimeoutSeconds = 15;
    public const int HandoffDeliveryTimeoutSeconds = 20;

    private readonly ApplicationState _state;
    private readonly ProjectService _projectService;
    private readonly ICodexBridge? _codexBridge;
    private readonly RelayPayloadStore _payloadStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HandoffCommand> _handoffCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _codexBindingRevisions = new();
    private string? _pairingCode;
    private DateTimeOffset? _pairingCodeExpiresAt;

    private sealed record CancelledHandoff(
        string Key,
        HandoffCommand Command,
        RelayTask? Task,
        RelayCommandDeliveryStatus? DeliveryStatus,
        string? DeliveryErrorCode,
        DateTimeOffset? UpdatedAt);

    public BrowserBridgeService(
        ApplicationState state,
        ProjectService projectService,
        ICodexBridge? codexBridge = null,
        RelayPayloadStore? payloadStore = null)
    {
        _state = state;
        _projectService = projectService;
        _codexBridge = codexBridge;
        _payloadStore = payloadStore ?? new RelayPayloadStore(
            projectService.StateFilePath.StartsWith("memory://", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Path.GetTempPath(), "BlueRelayTests", "relay", Guid.NewGuid().ToString("N"))
                : null);
        _state.BrowserBridge ??= new BrowserBridgeState();
        _state.BrowserBridge.PairedInstallationIds ??= [];
        _state.BrowserBridge.Bindings ??= [];
        _state.BrowserBridge.Tasks ??= [];
        HydratePayloads();
        if (_codexBridge is not null)
        {
            _codexBridge.ThreadChanged += CodexBridge_ThreadChanged;
        }
    }

    public event EventHandler? Changed;

    public ICodexBridge? CodexBridge => _codexBridge;

    public bool IsPaired => !string.IsNullOrWhiteSpace(_state.BrowserBridge.AuthToken);

    public PairingCodeInfo PairingCode => new(_pairingCode, _pairingCodeExpiresAt);

    public PairingCodeInfo GeneratePairingCode()
    {
        _pairingCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        _pairingCodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        return PairingCode;
    }

    public async Task<BridgeOperationResult> ResetPairingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousToken = _state.BrowserBridge.AuthToken;
            var previousInstallations = _state.BrowserBridge.PairedInstallationIds.ToList();
            var previousConnectionStates = _state.BrowserBridge.Bindings
                .Select(binding => (Binding: binding, Connected: binding.Connected))
                .ToList();
            _state.BrowserBridge.AuthToken = null;
            _state.BrowserBridge.PairedInstallationIds.Clear();
            foreach (var binding in _state.BrowserBridge.Bindings)
            {
                binding.Connected = false;
            }

            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                _state.BrowserBridge.AuthToken = previousToken;
                _state.BrowserBridge.PairedInstallationIds.AddRange(previousInstallations);
                foreach (var previous in previousConnectionStates)
                {
                    previous.Binding.Connected = previous.Connected;
                }

                return Failure("persistence_failed", saveResult.Error);
            }

            GeneratePairingCode();
            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsAuthorized(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_state.BrowserBridge.AuthToken))
        {
            return false;
        }

        var expected = System.Text.Encoding.UTF8.GetBytes(_state.BrowserBridge.AuthToken);
        var actual = System.Text.Encoding.UTF8.GetBytes(token);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public async Task<BridgeOperationResult<PairResponse>> PairAsync(
        string pairingCode,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            return Failure<PairResponse>("invalid_installation", "Installation id is required.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pairingCode is null || _pairingCodeExpiresAt is null || _pairingCodeExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Failure<PairResponse>("pairing_expired", "The pairing code has expired. Generate a new code in BlueRelay.");
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(_pairingCode),
                    System.Text.Encoding.UTF8.GetBytes(pairingCode.Trim())))
            {
                return Failure<PairResponse>("pairing_invalid", "The pairing code is not valid.");
            }

            var previousToken = _state.BrowserBridge.AuthToken;
            var previousPairingCode = _pairingCode;
            var previousPairingExpiry = _pairingCodeExpiresAt;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _state.BrowserBridge.AuthToken = token;
            var addedInstallation = false;
            if (!_state.BrowserBridge.PairedInstallationIds.Contains(installationId, StringComparer.Ordinal))
            {
                _state.BrowserBridge.PairedInstallationIds.Add(installationId);
                addedInstallation = true;
            }

            _pairingCode = null;
            _pairingCodeExpiresAt = null;
            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                _state.BrowserBridge.AuthToken = previousToken;
                if (addedInstallation)
                {
                    _state.BrowserBridge.PairedInstallationIds.Remove(installationId);
                }
                _pairingCode = previousPairingCode;
                _pairingCodeExpiresAt = previousPairingExpiry;
                return Failure<PairResponse>("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<PairResponse>(true, new PairResponse(token, installationId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult> RegisterTabAsync(
        RegisterTabRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedChatGptUrl(request.ChatGPTUrl))
        {
            return Failure("unsupported_origin", "Only ChatGPT pages can register with BlueRelay.");
        }

        if (string.IsNullOrWhiteSpace(request.InstallationId) || string.IsNullOrWhiteSpace(request.TabId))
        {
            return Failure("invalid_tab", "Installation id and tab id are required.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstallationPaired(request.InstallationId))
            {
                return Failure("not_paired", "This browser installation is not paired with BlueRelay.");
            }

            var binding = FindBinding(request.InstallationId, request.TabId);
            if (binding is null)
            {
                binding = new BrowserBinding
                {
                    InstallationId = request.InstallationId,
                    TabId = request.TabId
                };
                _state.BrowserBridge.Bindings.Add(binding);
            }

            binding.ChatGPTUrl = request.ChatGPTUrl;
            binding.ChatGPTConversationId = request.ChatGPTConversationId;
            binding.PageTitle = request.PageTitle ?? string.Empty;
            binding.LastSeenAt = DateTimeOffset.UtcNow;
            binding.Connected = true;
            var workstream = binding.WorkstreamId is { } workstreamId
                ? _projectService.FindWorkstreamForId(workstreamId)
                : null;
            binding.ConversationMismatch = workstream is not null && !IsConversationCompatible(workstream, binding.ChatGPTConversationId);
            var changed = workstream is not null && !binding.ConversationMismatch
                ? SyncWorkstreamPairing(workstream, binding, overwriteConversation: false)
                : true;
            if (changed)
            {
                var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
                if (!saveResult.Success)
                {
                    return Failure("persistence_failed", saveResult.Error);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult> HeartbeatAsync(
        string installationId,
        string tabId,
        string chatGPTUrl,
        string? conversationId,
        string pageTitle,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedChatGptUrl(chatGPTUrl))
        {
            return Failure("unsupported_origin", "Only ChatGPT pages can send a heartbeat.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstallationPaired(installationId))
            {
                return Failure("not_paired", "This browser installation is not paired with BlueRelay.");
            }

            var binding = FindBinding(installationId, tabId);
            if (binding is null)
            {
                return Failure("tab_not_registered", "Register the ChatGPT tab before sending a heartbeat.");
            }

            var previousConversationId = binding.ChatGPTConversationId;
            var previousUrl = binding.ChatGPTUrl;
            var previousTitle = binding.PageTitle;
            binding.ChatGPTUrl = chatGPTUrl;
            binding.ChatGPTConversationId = conversationId;
            binding.PageTitle = pageTitle ?? string.Empty;
            binding.LastSeenAt = DateTimeOffset.UtcNow;
            binding.Connected = true;
            var workstream = binding.WorkstreamId is { } workstreamId
                ? _projectService.FindWorkstreamForId(workstreamId)
                : null;
            binding.ConversationMismatch = workstream is not null && !IsConversationCompatible(workstream, conversationId);
            var changed = !string.Equals(previousConversationId, conversationId, StringComparison.Ordinal) ||
                          !string.Equals(previousUrl, chatGPTUrl, StringComparison.Ordinal) ||
                          !string.Equals(previousTitle, binding.PageTitle, StringComparison.Ordinal) ||
                          workstream is not null && !binding.ConversationMismatch && SyncWorkstreamPairing(workstream, binding, overwriteConversation: false);
            if (changed)
            {
                var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
                if (!saveResult.Success)
                {
                    return Failure("persistence_failed", saveResult.Error);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult> BindTabAsync(
        BindTabRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstallationPaired(request.InstallationId))
            {
                return Failure("not_paired", "This browser installation is not paired with BlueRelay.");
            }

            var binding = FindBinding(request.InstallationId, request.TabId);
            if (binding is null)
            {
                return Failure("tab_not_registered", "Register the ChatGPT tab before binding it.");
            }

            if (_projectService.FindWorkstreamForId(request.WorkstreamId) is not { } workstream)
            {
                return Failure("workstream_not_found", "The selected workstream no longer exists.");
            }

            MarkDisconnectedBindings();
            var assignedToAnotherWorkstream = binding.WorkstreamId is { } currentWorkstreamId && currentWorkstreamId != workstream.Id;
            if (assignedToAnotherWorkstream && !request.Rebind)
            {
                return Failure("tab_already_bound", "This ChatGPT tab is already bound to another Workstream.");
            }

            if (!IsConversationCompatible(workstream, binding.ChatGPTConversationId) && !request.Rebind)
            {
                return Failure("conversation_mismatch", "This Workstream is bound to another ChatGPT conversation.");
            }

            var existingAssignments = _state.BrowserBridge.Bindings
                .Where(item => item.WorkstreamId == workstream.Id && !ReferenceEquals(item, binding))
                .ToList();
            if (existingAssignments.Any(item => IsConnected(item)) && !request.Rebind)
            {
                return Failure("workstream_already_bound", "This Workstream is already bound to another active ChatGPT tab.");
            }

            var previousAssignments = _state.BrowserBridge.Bindings
                .Select(item => (Binding: item, WorkstreamId: item.WorkstreamId, ConversationMismatch: item.ConversationMismatch))
                .ToList();
            var previousPairing = CapturePairing(workstream);
            var cancelledHandoffs = new List<CancelledHandoff>();
            var changedConversation = !string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId) &&
                                      !string.Equals(workstream.ChatGPTConversationId, binding.ChatGPTConversationId, StringComparison.Ordinal);
            if (request.Rebind && changedConversation)
            {
                cancelledHandoffs = CancelHandoffCommandsForWorkstream(workstream.Id);
            }

            foreach (var other in existingAssignments)
            {
                other.WorkstreamId = null;
                other.ConversationMismatch = false;
            }

            binding.WorkstreamId = workstream.Id;
            binding.ConversationMismatch = false;
            SyncWorkstreamPairing(workstream, binding, overwriteConversation: request.Rebind);
            binding.LastSeenAt = DateTimeOffset.UtcNow;
            binding.Connected = true;
            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                foreach (var previous in previousAssignments)
                {
                    previous.Binding.WorkstreamId = previous.WorkstreamId;
                    previous.Binding.ConversationMismatch = previous.ConversationMismatch;
                }
                RestorePairing(workstream, previousPairing);
                RestoreCancelledHandoffs(cancelledHandoffs);

                return Failure("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult> UnbindWorkstreamAsync(
        Guid workstreamId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancelledHandoffs = CancelHandoffCommandsForWorkstream(workstreamId);
            var changed = cancelledHandoffs.Count > 0;
            var workstream = _projectService.FindWorkstreamForId(workstreamId);
            var previousPairing = workstream is null ? null : CapturePairing(workstream);
            var previousBindings = _state.BrowserBridge.Bindings
                .Where(item => item.WorkstreamId == workstreamId)
                .Select(item => (Binding: item, ConversationMismatch: item.ConversationMismatch))
                .ToList();
            foreach (var binding in _state.BrowserBridge.Bindings.Where(item => item.WorkstreamId == workstreamId))
            {
                binding.WorkstreamId = null;
                binding.ConversationMismatch = false;
                changed = true;
            }

            if (workstream is not null && HasPairing(workstream))
            {
                ClearChatGPTPairing(workstream);
                changed = true;
            }

            if (!changed)
            {
                return new BridgeOperationResult(true);
            }

            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                RestoreCancelledHandoffs(cancelledHandoffs);
                if (workstream is not null && previousPairing is not null)
                {
                    RestorePairing(workstream, previousPairing);
                }
                foreach (var previous in previousBindings)
                {
                    previous.Binding.WorkstreamId = workstreamId;
                    previous.Binding.ConversationMismatch = previous.ConversationMismatch;
                }
                return Failure("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<RelayTask>> CaptureTaskAsync(
        CaptureTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!BrowserTaskParser.TryNormalize(request.Prompt, out var prompt))
        {
            return Failure<RelayTask>("not_a_codex_task", $"The task must contain the marker {BrowserTaskParser.Marker}.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var binding = FindBinding(request.InstallationId, request.TabId);
            if (binding?.WorkstreamId is not { } workstreamId)
            {
                return Failure<RelayTask>("tab_not_bound", "Bind this ChatGPT tab to a Workstream before capturing a task.");
            }

            var workstream = _projectService.FindWorkstreamForId(workstreamId);
            if (workstream is null)
            {
                binding.WorkstreamId = null;
                return Failure<RelayTask>("workstream_not_found", "The bound Workstream no longer exists.");
            }

            if (binding.ConversationMismatch || !IsConversationCompatible(workstream, binding.ChatGPTConversationId))
            {
                return Failure<RelayTask>("conversation_mismatch", "The current ChatGPT conversation is not the one bound to this Workstream.");
            }

            if (!string.IsNullOrWhiteSpace(request.ChatGPTConversationId) &&
                !string.Equals(request.ChatGPTConversationId, binding.ChatGPTConversationId, StringComparison.Ordinal))
            {
                return Failure<RelayTask>("conversation_mismatch", "The current ChatGPT conversation is not the one bound to this Workstream.");
            }

            var previousTask = FindCurrentTask(workstream);
            if (previousTask is not null && BrowserTaskParser.AreEquivalentPayloads(previousTask.Prompt, prompt))
            {
                return new BridgeOperationResult<RelayTask>(true, previousTask);
            }

            var cancelledHandoffs = CancelHandoffCommandsForWorkstream(workstream.Id);
            var previousTaskDeliveryStatus = previousTask?.DeliveryStatus;
            var previousTaskDeliveryErrorCode = previousTask?.DeliveryErrorCode;
            var previousTaskUpdatedAt = previousTask?.UpdatedAt;
            var previousCodexProgress = workstream.CodexProgress;
            var previousCodexError = workstream.CodexError;
            var previousCodexErrorCode = workstream.CodexErrorCode;

            var task = new RelayTask
            {
                WorkstreamId = workstream.Id,
                Prompt = prompt,
                SourceTabKey = binding.TabKey,
                SourceTabId = binding.TabId,
                SourceChatGPTUrl = request.ChatGPTUrl,
                CapturedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Status = RelayTaskStatus.Captured
            };
            task.Payload = await _payloadStore.WriteAsync(
                workstream.Id,
                task.Id,
                "task.md",
                prompt,
                cancellationToken).ConfigureAwait(false);
            var previousTaskId = workstream.CurrentTaskId;
            _state.BrowserBridge.Tasks.Add(task);
            workstream.CurrentTaskId = task.Id.ToString("D");
            workstream.CodexProgress = null;
            workstream.CodexError = null;
            workstream.CodexErrorCode = null;

            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.ReadyForCodex,
                manualOverride: true,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                workstream.CurrentTaskId = previousTaskId;
                workstream.CodexProgress = previousCodexProgress;
                workstream.CodexError = previousCodexError;
                workstream.CodexErrorCode = previousCodexErrorCode;
                _state.BrowserBridge.Tasks.Remove(task);
                if (previousTask is not null)
                {
                    previousTask.DeliveryStatus = previousTaskDeliveryStatus!.Value;
                    previousTask.DeliveryErrorCode = previousTaskDeliveryErrorCode;
                    previousTask.UpdatedAt = previousTaskUpdatedAt!.Value;
                }

                RestoreCancelledHandoffs(cancelledHandoffs);
                return Failure<RelayTask>("state_transition_failed", stateResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<RelayTask>(true, task);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<RelayTask>> ConfirmTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        return await ChangeTaskStateAsync(taskId, WorkflowState.CodexRunning, RelayTaskStatus.CodexRunning, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BridgeOperationResult<RelayTask>> SendTaskToCodexAsync(
        Guid taskId,
        string? userNote = null,
        CancellationToken cancellationToken = default)
    {
        if (_codexBridge is null)
        {
            return Failure<RelayTask>("codex_unavailable", "The real Codex App Server bridge is not configured.");
        }

        RelayTask? task;
        Project? project;
        Workstream? workstream;
        string? initialCodexThreadId;
        long codexBindingRevision;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            task = FindTask(taskId);
            workstream = task is null ? null : _projectService.FindWorkstreamForId(task.WorkstreamId);
            project = workstream is null ? null : _projectService.Find(workstream.ProjectId);
            if (task is null || workstream is null || project is null)
            {
                return Failure<RelayTask>("task_not_found", "The task or Workstream no longer exists.");
            }

            if (!IsCurrentTask(workstream, task) ||
                workstream.CurrentState is not (WorkflowState.ReadyForCodex or WorkflowState.NeedsAttention or WorkflowState.Error))
            {
                return Failure<RelayTask>("task_not_ready", "Only the current task in ReadyForCodex can be sent to Codex.");
            }

            task.UserNote = string.IsNullOrWhiteSpace(userNote) ? null : userNote.Trim();
            initialCodexThreadId = workstream.CodexThreadId;
            codexBindingRevision = GetCodexBindingRevision(workstream.Id);
            task.CodexError = null;
            task.Status = RelayTaskStatus.CodexRunning;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            workstream.CodexProgress = "准备启动 Codex";
            workstream.CodexError = null;
            workstream.CodexErrorCode = null;
            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.CodexRunning,
                manualOverride: false,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                return Failure<RelayTask>("state_transition_failed", stateResult.Error);
            }

            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                return Failure<RelayTask>("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }

        var combinedPrompt = RelayPromptComposer.Compose(task!.UserNote, task.Prompt);
        CodexTurnResult codexResult;
        try
        {
            codexResult = await _codexBridge.SubmitTaskAsync(
                new CodexTaskRequest(project!, workstream!, task, combinedPrompt),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            codexResult = new CodexTurnResult(
                false,
                workstream!.CodexThreadId,
                task.CodexTurnId,
                null,
                exception.Message,
                ErrorCode: "codex_bridge_failed");
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var currentTask = FindTask(taskId);
            var currentWorkstream = currentTask is null ? null : _projectService.FindWorkstreamForId(currentTask.WorkstreamId);
            if (currentTask is null || currentWorkstream is null)
            {
                return Failure<RelayTask>("task_not_found", "The task or Workstream no longer exists.");
            }

            currentWorkstream.CodexProgress = codexResult.Success ? "Codex 已完成" : null;
            currentWorkstream.CodexError = codexResult.Error;
            currentWorkstream.CodexErrorCode = codexResult.ErrorCode;
            currentTask.CodexTurnId = codexResult.TurnId;
            currentTask.CodexError = codexResult.Error;
            if (!string.IsNullOrWhiteSpace(codexResult.ThreadId) &&
                GetCodexBindingRevision(currentWorkstream.Id) == codexBindingRevision &&
                string.Equals(currentWorkstream.CodexThreadId, initialCodexThreadId, StringComparison.Ordinal))
            {
                currentWorkstream.CodexThreadId = codexResult.ThreadId;
                currentWorkstream.CodexSessionId = codexResult.ThreadId;
            }

            if (codexResult.Success && !string.IsNullOrWhiteSpace(codexResult.Result))
            {
                currentWorkstream.CodexErrorCode = null;
                currentTask.Result = codexResult.Result.Trim();
                currentTask.ResultPayload = await _payloadStore.WriteAsync(
                    currentWorkstream.Id,
                    currentTask.Id,
                    "result.md",
                    currentTask.Result,
                    CancellationToken.None).ConfigureAwait(false);
                currentTask.Status = RelayTaskStatus.ReadyForChatGPT;
                currentTask.DeliveryStatus = RelayCommandDeliveryStatus.None;
                currentTask.DeliveryErrorCode = null;
                currentTask.UpdatedAt = DateTimeOffset.UtcNow;
                var stateResult = await _projectService.TryChangeStateAsync(
                    currentWorkstream.ProjectId,
                    currentWorkstream.Id,
                    WorkflowState.ReadyForChatGPT,
                    manualOverride: false,
                    CancellationToken.None).ConfigureAwait(false);
                if (!stateResult.Success)
                {
                    currentTask.Status = RelayTaskStatus.Error;
                    currentTask.CodexError = stateResult.Error;
                    currentWorkstream.CodexError = stateResult.Error;
                    currentWorkstream.CodexErrorCode = "codex_state_transition_failed";
                    currentWorkstream.CurrentState = WorkflowState.Error;
                    currentWorkstream.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                currentTask.Status = RelayTaskStatus.Error;
                currentTask.UpdatedAt = DateTimeOffset.UtcNow;
                var targetState = RequiresCodexAttention(codexResult)
                    ? WorkflowState.NeedsAttention
                    : WorkflowState.Error;
                var stateResult = await _projectService.TryChangeStateAsync(
                    currentWorkstream.ProjectId,
                    currentWorkstream.Id,
                    targetState,
                    manualOverride: false,
                    CancellationToken.None).ConfigureAwait(false);
                if (!stateResult.Success)
                {
                    currentWorkstream.CurrentState = targetState;
                    currentWorkstream.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            var saveResult = await _projectService.TrySaveAsync(CancellationToken.None).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                return Failure<RelayTask>("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return codexResult.Success
                ? new BridgeOperationResult<RelayTask>(true, currentTask)
                : new BridgeOperationResult<RelayTask>(
                    false,
                    currentTask,
                    codexResult.ErrorCode ?? (codexResult.Cancelled ? "codex_cancelled" : "codex_failed"),
                    codexResult.Error ?? "Codex did not return a result.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult> CancelCodexTaskAsync(
        Guid workstreamId,
        CancellationToken cancellationToken = default)
    {
        if (_codexBridge is null)
        {
            return Failure("codex_unavailable", "The real Codex App Server bridge is not configured.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? threadId;
        string? turnId;
        try
        {
            var workstream = _projectService.FindWorkstreamForId(workstreamId);
            var task = workstream is null ? null : FindCurrentTask(workstream);
            threadId = workstream?.CodexThreadId;
            turnId = task?.CodexTurnId;
        }
        finally
        {
            _gate.Release();
        }

        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return Failure("codex_turn_not_found", "There is no active Codex turn to cancel.");
        }

        return await _codexBridge.InterruptAsync(threadId, turnId, cancellationToken).ConfigureAwait(false)
            ? new BridgeOperationResult(true)
            : Failure("codex_cancel_failed", "BlueRelay could not cancel the Codex turn.");
    }

    public async Task<BridgeOperationResult> ResetCodexThreadAsync(
        Guid workstreamId,
        CancellationToken cancellationToken = default)
    {
        if (_codexBridge is null)
        {
            return Failure("codex_unavailable", "The real Codex App Server bridge is not configured.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workstream = _projectService.FindWorkstreamForId(workstreamId);
            if (workstream is null)
            {
                return Failure("workstream_not_found", "The selected Workstream no longer exists.");
            }

            AdvanceCodexBindingRevision(workstream.Id);
            await _codexBridge.ResetThreadAsync(workstream, cancellationToken).ConfigureAwait(false);
            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                return Failure("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<RelayTask>> NewCodexSessionAndRetryAsync(
        Guid workstreamId,
        string? userNote = null,
        CancellationToken cancellationToken = default)
    {
        if (_codexBridge is null)
        {
            return Failure<RelayTask>("codex_unavailable", "The real Codex App Server bridge is not configured.");
        }

        RelayTask? task;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workstream = _projectService.FindWorkstreamForId(workstreamId);
            task = workstream is null ? null : FindCurrentTask(workstream);
            if (workstream is null || task is null)
            {
                return Failure<RelayTask>("task_not_found", "The current Workstream task no longer exists.");
            }

            if (workstream.CurrentState == WorkflowState.CodexRunning)
            {
                return Failure<RelayTask>("codex_turn_active", "Cancel the active Codex turn before starting a new session.");
            }

            await _codexBridge.ResetThreadAsync(workstream, cancellationToken).ConfigureAwait(false);
            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                return Failure<RelayTask>("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }

        return await SendTaskToCodexAsync(task!.Id, userNote ?? task.UserNote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BridgeOperationResult<RelayTask>> SimulateResultAsync(
        Guid taskId,
        string result,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return Failure<RelayTask>("result_empty", "A simulated Codex result is required.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return Failure<RelayTask>("task_not_found", "The task no longer exists.");
            }

            var workstream = _projectService.FindWorkstreamForId(task.WorkstreamId);
            if (workstream is null)
            {
                return Failure<RelayTask>("workstream_not_found", "The task Workstream no longer exists.");
            }

            if (!IsCurrentTask(workstream, task))
            {
                return Failure<RelayTask>("task_not_current", "Only the current Workstream task can receive a result.");
            }

            var previousResult = task.Result;
            var previousStatus = task.Status;
            var previousDeliveryStatus = task.DeliveryStatus;
            var previousDeliveryErrorCode = task.DeliveryErrorCode;
            task.Result = result.Trim();
            task.ResultPayload = await _payloadStore.WriteAsync(
                workstream.Id,
                task.Id,
                "result.md",
                task.Result,
                cancellationToken).ConfigureAwait(false);
            task.Status = RelayTaskStatus.ReadyForChatGPT;
            task.DeliveryStatus = RelayCommandDeliveryStatus.None;
            task.DeliveryErrorCode = null;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.ReadyForChatGPT,
                manualOverride: false,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                task.Result = previousResult;
                task.Status = previousStatus;
                task.DeliveryStatus = previousDeliveryStatus;
                task.DeliveryErrorCode = previousDeliveryErrorCode;
                return Failure<RelayTask>("state_transition_failed", stateResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<RelayTask>(true, task);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<HandoffCommand>> QueueHandoffAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = FindTask(taskId);
            if (task is null)
            {
                return Failure<HandoffCommand>("task_not_found", "The task no longer exists.");
            }

            var workstream = _projectService.FindWorkstreamForId(task.WorkstreamId);
            if (workstream is null || !IsCurrentTask(workstream, task))
            {
                return Failure<HandoffCommand>("task_not_current", "Only the current Workstream task can be sent to ChatGPT.");
            }

            if (workstream.CurrentState != WorkflowState.ReadyForChatGPT)
            {
                return Failure<HandoffCommand>("task_not_ready", "Only a result ready for ChatGPT can be sent back.");
            }

            if (string.IsNullOrWhiteSpace(task.Result))
            {
                return Failure<HandoffCommand>("result_missing", "There is no result to send back to ChatGPT.");
            }

            MarkDisconnectedBindings();
            var binding = _state.BrowserBridge.Bindings.FirstOrDefault(item => item.WorkstreamId == task.WorkstreamId);
            if (binding is null)
            {
                return Failure<HandoffCommand>("tab_not_bound", "The ChatGPT conversation is not currently bound to this Workstream.");
            }

            if (binding.ConversationMismatch || !IsConversationCompatible(workstream, binding.ChatGPTConversationId))
            {
                return Failure<HandoffCommand>("conversation_mismatch", "The original ChatGPT conversation is not currently connected.");
            }

            if (!IsConnected(binding))
            {
                return Failure<HandoffCommand>("tab_disconnected", "The ChatGPT tab is disconnected. Keep the result and reconnect the conversation.");
            }

            var command = new HandoffCommand(
                Guid.NewGuid(),
                task.Id,
                task.WorkstreamId,
                binding.InstallationId,
                binding.TabId,
                binding.ChatGPTUrl,
                binding.ChatGPTConversationId,
                RelayPromptComposer.ComposeResult(task.ResultNote, task.Result),
                RelayCommandDeliveryStatus.Queued,
                0,
                null);
            var previousDeliveryStatus = task.DeliveryStatus;
            var previousDeliveryErrorCode = task.DeliveryErrorCode;
            var previousCommand = _handoffCommands.TryGetValue(binding.TabKey, out var existingCommand)
                ? existingCommand
                : null;
            task.DeliveryStatus = RelayCommandDeliveryStatus.Queued;
            task.DeliveryErrorCode = null;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            _handoffCommands[binding.TabKey] = command;

            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                task.DeliveryStatus = previousDeliveryStatus;
                task.DeliveryErrorCode = previousDeliveryErrorCode;
                if (previousCommand is null)
                {
                    _handoffCommands.Remove(binding.TabKey);
                }
                else
                {
                    _handoffCommands[binding.TabKey] = previousCommand;
                }

                return Failure<HandoffCommand>("persistence_failed", saveResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<HandoffCommand>(true, command);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<HandoffCommand>> GetNextCommandAsync(
        string installationId,
        string tabId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var binding = FindBinding(installationId, tabId);
            if (binding is null)
            {
                return Failure<HandoffCommand>("tab_not_registered", "The tab is not registered.");
            }

            if (binding.WorkstreamId is { } boundWorkstreamId &&
                _projectService.FindWorkstreamForId(boundWorkstreamId) is { } boundWorkstream &&
                (binding.ConversationMismatch || !IsConversationCompatible(boundWorkstream, binding.ChatGPTConversationId)))
            {
                return Failure<HandoffCommand>("conversation_mismatch", "The current ChatGPT conversation is not the one bound to this Workstream.");
            }

            if (!_handoffCommands.TryGetValue(binding.TabKey, out var command))
            {
                var candidate = _handoffCommands.FirstOrDefault(item =>
                    item.Value.WorkstreamId == binding.WorkstreamId &&
                    string.Equals(item.Value.ChatGPTConversationId, binding.ChatGPTConversationId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(binding.ChatGPTConversationId));
                if (candidate.Value is null)
                {
                    return new BridgeOperationResult<HandoffCommand>(true);
                }

                _handoffCommands.Remove(candidate.Key);
                command = candidate.Value with
                {
                    InstallationId = binding.InstallationId,
                    TabId = binding.TabId,
                    ChatGPTUrl = binding.ChatGPTUrl,
                    ChatGPTConversationId = binding.ChatGPTConversationId,
                    DeliveryStatus = RelayCommandDeliveryStatus.Queued,
                    AttemptCount = 0,
                    LastAttemptAt = null
                };
                _handoffCommands[binding.TabKey] = command;
                var migratedTask = FindTask(command.TaskId);
                if (migratedTask is not null)
                {
                    migratedTask.DeliveryStatus = RelayCommandDeliveryStatus.Queued;
                    migratedTask.DeliveryErrorCode = null;
                }
            }

            var commandTask = FindTask(command.TaskId);
            var commandWorkstream = commandTask is null
                ? null
                : _projectService.FindWorkstreamForId(commandTask.WorkstreamId);
            if (commandTask is null || commandWorkstream is null || !IsCurrentTask(commandWorkstream, commandTask))
            {
                _handoffCommands.Remove(binding.TabKey);
                return new BridgeOperationResult<HandoffCommand>(true);
            }

            var now = DateTimeOffset.UtcNow;
            if (command.DeliveryStatus == RelayCommandDeliveryStatus.Delivering)
            {
                if (command.LastAttemptAt is not { } lastAttemptAt ||
                    now - lastAttemptAt < TimeSpan.FromSeconds(HandoffDeliveryTimeoutSeconds))
                {
                    return new BridgeOperationResult<HandoffCommand>(true);
                }

                _handoffCommands.Remove(binding.TabKey);
                commandTask.DeliveryStatus = RelayCommandDeliveryStatus.Failed;
                commandTask.DeliveryErrorCode = "delivery_timeout";
                commandTask.UpdatedAt = now;
                await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);

                return new BridgeOperationResult<HandoffCommand>(true);
            }

            var deliveringCommand = command with
            {
                DeliveryStatus = RelayCommandDeliveryStatus.Delivering,
                AttemptCount = command.AttemptCount + 1,
                LastAttemptAt = now
            };
            _handoffCommands[binding.TabKey] = deliveringCommand;
            commandTask.DeliveryStatus = RelayCommandDeliveryStatus.Delivering;
            commandTask.DeliveryErrorCode = null;
            commandTask.UpdatedAt = now;
            await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);

            return new BridgeOperationResult<HandoffCommand>(true, deliveringCommand);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<RelayTask>> AcknowledgeHandoffAsync(
        Guid commandId,
        bool success,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var commandEntry = _handoffCommands.FirstOrDefault(item => item.Value.CommandId == commandId);
            if (commandEntry.Value is null)
            {
                return Failure<RelayTask>("command_not_found", "The handoff command is no longer pending.");
            }

            var task = FindTask(commandEntry.Value.TaskId);
            var workstream = task is null ? null : _projectService.FindWorkstreamForId(task.WorkstreamId);
            if (task is null || workstream is null)
            {
                return Failure<RelayTask>("task_not_found", "The task no longer exists.");
            }

            if (!IsCurrentTask(workstream, task))
            {
                _handoffCommands.Remove(commandEntry.Key);
                return Failure<RelayTask>("task_not_current", "The handoff belongs to an older Workstream task.");
            }

            if (!success)
            {
                task.DeliveryStatus = RelayCommandDeliveryStatus.Failed;
                task.DeliveryErrorCode = NormalizeDeliveryErrorCode(errorCode);
                task.UpdatedAt = DateTimeOffset.UtcNow;
                _handoffCommands.Remove(commandEntry.Key);
                var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
                if (!saveResult.Success)
                {
                    return Failure<RelayTask>("persistence_failed", saveResult.Error);
                }

                Changed?.Invoke(this, EventArgs.Empty);
                return new BridgeOperationResult<RelayTask>(true, task);
            }

            var previousStatus = task.Status;
            var previousDeliveryStatus = task.DeliveryStatus;
            var previousDeliveryErrorCode = task.DeliveryErrorCode;
            task.Status = RelayTaskStatus.ChatGPTReviewing;
            task.DeliveryStatus = RelayCommandDeliveryStatus.Delivered;
            task.DeliveryErrorCode = null;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.ChatGPTReviewing,
                manualOverride: false,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                task.Status = previousStatus;
                task.DeliveryStatus = previousDeliveryStatus;
                task.DeliveryErrorCode = previousDeliveryErrorCode;
                return Failure<RelayTask>("state_transition_failed", stateResult.Error);
            }

            _handoffCommands.Remove(commandEntry.Key);
            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<RelayTask>(true, task);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<RelayTask>> CompleteTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = FindTask(taskId);
            var workstream = task is null ? null : _projectService.FindWorkstreamForId(task.WorkstreamId);
            if (task is null || workstream is null)
            {
                return Failure<RelayTask>("task_not_found", "The task or Workstream no longer exists.");
            }

            if (!IsCurrentTask(workstream, task))
            {
                return Failure<RelayTask>("task_not_current", "Only the current Workstream task can be completed.");
            }

            var cancelledHandoffs = CancelHandoffCommandsForWorkstream(workstream.Id);
            var previousStatus = task.Status;
            var previousDeliveryStatus = task.DeliveryStatus;
            var previousDeliveryErrorCode = task.DeliveryErrorCode;
            var previousUpdatedAt = task.UpdatedAt;
            task.Status = RelayTaskStatus.Completed;
            if (cancelledHandoffs.Count > 0 || task.DeliveryStatus is RelayCommandDeliveryStatus.Queued or RelayCommandDeliveryStatus.Delivering)
            {
                task.DeliveryStatus = RelayCommandDeliveryStatus.None;
                task.DeliveryErrorCode = null;
            }

            task.UpdatedAt = DateTimeOffset.UtcNow;
            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.Completed,
                manualOverride: true,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                task.Status = previousStatus;
                task.DeliveryStatus = previousDeliveryStatus;
                task.DeliveryErrorCode = previousDeliveryErrorCode;
                task.UpdatedAt = previousUpdatedAt;
                RestoreCancelledHandoffs(cancelledHandoffs);
                return Failure<RelayTask>("state_transition_failed", stateResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<RelayTask>(true, task);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult> ClearCurrentTaskAsync(
        Guid workstreamId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workstream = _projectService.FindWorkstreamForId(workstreamId);
            if (workstream is null)
            {
                return Failure("workstream_not_found", "The selected Workstream no longer exists.");
            }

            var currentTask = FindCurrentTask(workstream);
            var previousTaskId = workstream.CurrentTaskId;
            var previousTaskDeliveryStatus = currentTask?.DeliveryStatus;
            var previousTaskDeliveryErrorCode = currentTask?.DeliveryErrorCode;
            var previousTaskUpdatedAt = currentTask?.UpdatedAt;
            var cancelledHandoffs = CancelHandoffCommandsForWorkstream(workstream.Id);
            workstream.CurrentTaskId = null;
            if (currentTask is not null)
            {
                currentTask.DeliveryStatus = RelayCommandDeliveryStatus.None;
                currentTask.DeliveryErrorCode = null;
                currentTask.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.Idle,
                manualOverride: true,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                workstream.CurrentTaskId = previousTaskId;
                if (currentTask is not null)
                {
                    currentTask.DeliveryStatus = previousTaskDeliveryStatus!.Value;
                    currentTask.DeliveryErrorCode = previousTaskDeliveryErrorCode;
                    currentTask.UpdatedAt = previousTaskUpdatedAt!.Value;
                }

                RestoreCancelledHandoffs(cancelledHandoffs);
                return Failure("state_transition_failed", stateResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<BridgeWorkstreamDto> ListWorkstreams()
    {
        MarkDisconnectedBindings();
        return _state.Projects
            .SelectMany(project => project.Workstreams.Select(workstream =>
            {
                var task = FindCurrentTask(workstream);
                var binding = _state.BrowserBridge.Bindings.FirstOrDefault(item => item.WorkstreamId == workstream.Id);
                return new BridgeWorkstreamDto(
                    project.Id,
                    project.Name,
                    workstream.Id,
                    workstream.Name,
                    workstream.CurrentState,
                    workstream.CurrentTaskId,
                    task?.Prompt,
                    task?.Result,
                    workstream.ChatGPTConversationId,
                    workstream.ChatGPTUrl,
                    workstream.ChatGPTTitle,
                    workstream.CodexThreadId,
                    binding is null ? null : ToDto(binding));
            }))
            .ToList();
    }

    public BrowserBindingDto? FindBindingDto(Guid workstreamId)
    {
        MarkDisconnectedBindings();
        var binding = _state.BrowserBridge.Bindings.FirstOrDefault(item => item.WorkstreamId == workstreamId);
        return binding is null ? null : ToDto(binding);
    }

    public RelayTask? FindCurrentTask(Guid workstreamId)
    {
        var workstream = _projectService.FindWorkstreamForId(workstreamId);
        return workstream is null ? null : FindCurrentTask(workstream);
    }

    public RelayTask? FindTask(Guid taskId)
    {
        return _state.BrowserBridge.Tasks.FirstOrDefault(task => task.Id == taskId);
    }

    public RelayTask? FindTaskByWorkstream(Guid workstreamId)
    {
        var workstream = _projectService.FindWorkstreamForId(workstreamId);
        return workstream is null ? null : FindCurrentTask(workstream);
    }

    public bool IsInstallationPaired(string installationId)
    {
        return IsPaired && _state.BrowserBridge.PairedInstallationIds.Contains(installationId, StringComparer.Ordinal);
    }

    public static bool IsSupportedChatGptUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "chat.openai.com", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BridgeOperationResult<RelayTask>> ChangeTaskStateAsync(
        Guid taskId,
        WorkflowState targetState,
        RelayTaskStatus targetStatus,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var task = FindTask(taskId);
            var workstream = task is null ? null : _projectService.FindWorkstreamForId(task.WorkstreamId);
            if (task is null || workstream is null)
            {
                return Failure<RelayTask>("task_not_found", "The task or Workstream no longer exists.");
            }

            if (!IsCurrentTask(workstream, task))
            {
                return Failure<RelayTask>("task_not_current", "Only the current Workstream task can change state.");
            }

            var previousStatus = task.Status;
            task.Status = targetStatus;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                targetState,
                manualOverride: false,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                task.Status = previousStatus;
                return Failure<RelayTask>("state_transition_failed", stateResult.Error);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new BridgeOperationResult<RelayTask>(true, task);
        }
        finally
        {
            _gate.Release();
        }
    }

    private BrowserBinding? FindBinding(string installationId, string tabId)
    {
        return _state.BrowserBridge.Bindings.FirstOrDefault(item =>
            string.Equals(item.InstallationId, installationId, StringComparison.Ordinal)
            && string.Equals(item.TabId, tabId, StringComparison.Ordinal));
    }

    private RelayTask? FindCurrentTask(Workstream workstream)
    {
        return Guid.TryParse(workstream.CurrentTaskId, out var taskId)
            ? FindTask(taskId)
            : null;
    }

    private static bool IsCurrentTask(Workstream workstream, RelayTask task)
    {
        return Guid.TryParse(workstream.CurrentTaskId, out var currentTaskId)
            && currentTaskId == task.Id;
    }

    private List<CancelledHandoff> CancelHandoffCommandsForWorkstream(Guid workstreamId)
    {
        var cancelled = _handoffCommands
            .Where(item => item.Value.WorkstreamId == workstreamId)
            .Select(item =>
            {
                var task = FindTask(item.Value.TaskId);
                return new CancelledHandoff(
                    item.Key,
                    item.Value,
                    task,
                    task?.DeliveryStatus,
                    task?.DeliveryErrorCode,
                    task?.UpdatedAt);
            })
            .ToList();

        foreach (var item in cancelled)
        {
            _handoffCommands.Remove(item.Key);
            if (item.Task is not null && item.Task.DeliveryStatus is RelayCommandDeliveryStatus.Queued or RelayCommandDeliveryStatus.Delivering)
            {
                item.Task.DeliveryStatus = RelayCommandDeliveryStatus.None;
                item.Task.DeliveryErrorCode = null;
                item.Task.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        return cancelled;
    }

    private void RestoreCancelledHandoffs(IEnumerable<CancelledHandoff> cancelled)
    {
        foreach (var item in cancelled)
        {
            _handoffCommands[item.Key] = item.Command;
            if (item.Task is not null && item.DeliveryStatus.HasValue && item.UpdatedAt.HasValue)
            {
                item.Task.DeliveryStatus = item.DeliveryStatus.Value;
                item.Task.DeliveryErrorCode = item.DeliveryErrorCode;
                item.Task.UpdatedAt = item.UpdatedAt.Value;
            }
        }
    }

    private static bool IsConnected(BrowserBinding binding)
    {
        return binding.Connected && binding.LastSeenAt >= DateTimeOffset.UtcNow.AddSeconds(-BindingTimeoutSeconds);
    }

    private void MarkDisconnectedBindings()
    {
        foreach (var binding in _state.BrowserBridge.Bindings)
        {
            binding.Connected = IsConnected(binding);
        }
    }

    private static BrowserBindingDto ToDto(BrowserBinding binding)
    {
        return new BrowserBindingDto(
            binding.InstallationId,
            binding.TabId,
            binding.TabKey,
            binding.WorkstreamId,
            binding.ChatGPTUrl,
            binding.ChatGPTConversationId,
            binding.PageTitle,
            binding.LastSeenAt,
            IsConnected(binding),
            binding.ConversationMismatch);
    }

    private sealed record PairingSnapshot(
        string? BrowserInstallationId,
        string? ChatGPTConversationId,
        string? ChatGPTTabId,
        string? ChatGPTUrl,
        string? ChatGPTTitle);

    private static PairingSnapshot CapturePairing(Workstream workstream)
    {
        return new PairingSnapshot(
            workstream.BrowserInstallationId,
            workstream.ChatGPTConversationId,
            workstream.ChatGPTTabId,
            workstream.ChatGPTUrl,
            workstream.ChatGPTTitle);
    }

    private static void RestorePairing(Workstream workstream, PairingSnapshot snapshot)
    {
        workstream.BrowserInstallationId = snapshot.BrowserInstallationId;
        workstream.ChatGPTConversationId = snapshot.ChatGPTConversationId;
        workstream.ChatGPTTabId = snapshot.ChatGPTTabId;
        workstream.ChatGPTUrl = snapshot.ChatGPTUrl;
        workstream.ChatGPTTitle = snapshot.ChatGPTTitle;
    }

    private static bool HasPairing(Workstream workstream)
    {
        return !string.IsNullOrWhiteSpace(workstream.BrowserInstallationId) ||
               !string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId) ||
               !string.IsNullOrWhiteSpace(workstream.ChatGPTTabId) ||
               !string.IsNullOrWhiteSpace(workstream.ChatGPTUrl) ||
               !string.IsNullOrWhiteSpace(workstream.ChatGPTTitle);
    }

    private static void ClearChatGPTPairing(Workstream workstream)
    {
        workstream.BrowserInstallationId = null;
        workstream.ChatGPTConversationId = null;
        workstream.ChatGPTTabId = null;
        workstream.ChatGPTUrl = null;
        workstream.ChatGPTTitle = null;
    }

    private static bool SyncWorkstreamPairing(
        Workstream workstream,
        BrowserBinding binding,
        bool overwriteConversation)
    {
        var previous = CapturePairing(workstream);
        workstream.BrowserInstallationId = binding.InstallationId;
        workstream.ChatGPTTabId = binding.TabId;
        if (overwriteConversation || string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId))
        {
            workstream.ChatGPTConversationId = binding.ChatGPTConversationId;
        }

        workstream.ChatGPTUrl = binding.ChatGPTUrl;
        workstream.ChatGPTTitle = binding.PageTitle;
        return previous != CapturePairing(workstream);
    }

    private static bool IsConversationCompatible(Workstream workstream, string? conversationId)
    {
        return string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId) ||
               !string.IsNullOrWhiteSpace(conversationId) &&
               string.Equals(workstream.ChatGPTConversationId, conversationId, StringComparison.Ordinal);
    }

    private static BridgeOperationResult Failure(string code, string message) => new(false, code, message);

    private static BridgeOperationResult<T> Failure<T>(string code, string message) => new(false, default, code, message);

    private static string NormalizeDeliveryErrorCode(string? errorCode)
    {
        return string.IsNullOrWhiteSpace(errorCode) ? "injection_failed" : errorCode.Trim();
    }

    private static bool RequiresCodexAttention(CodexTurnResult result)
    {
        return result.Cancelled || result.ErrorCode is
            "codex_thread_conflict" or
            "codex_thread_archived" or
            "codex_thread_resume_failed";
    }

    private void HydratePayloads()
    {
        foreach (var task in _state.BrowserBridge.Tasks)
        {
            if (task.Payload is not null && string.IsNullOrEmpty(task.Prompt))
            {
                task.Prompt = _payloadStore.Read(task.Payload) ?? string.Empty;
            }

            if (task.ResultPayload is not null && string.IsNullOrEmpty(task.Result))
            {
                task.Result = _payloadStore.Read(task.ResultPayload);
            }
        }
    }

    private async void CodexBridge_ThreadChanged(object? sender, CodexThreadUpdate update)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var workstream = _projectService.FindWorkstreamForId(update.WorkstreamId);
                if (workstream is null ||
                    !string.Equals(workstream.CodexThreadId, update.ThreadId, StringComparison.Ordinal))
                {
                    return;
                }

                await _projectService.TrySaveAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // The active turn will report the final persistence failure if needed.
        }
    }

    private long GetCodexBindingRevision(Guid workstreamId)
    {
        return _codexBindingRevisions.TryGetValue(workstreamId, out var revision) ? revision : 0;
    }

    private long AdvanceCodexBindingRevision(Guid workstreamId)
    {
        var revision = GetCodexBindingRevision(workstreamId) + 1;
        _codexBindingRevisions[workstreamId] = revision;
        return revision;
    }
}
