using System.Security.Cryptography;
using BlueRelay.Models;

namespace BlueRelay.Services.Bridges;

/// <summary>
/// Owns browser-tab bindings and current relay tasks. It deliberately sits outside
/// the WPF window so HTTP requests and UI commands use the same state transitions.
/// </summary>
public sealed class BrowserBridgeService
{
    public const int DefaultPort = 48917;
    public const int BindingTimeoutSeconds = 15;

    private readonly ApplicationState _state;
    private readonly ProjectService _projectService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HandoffCommand> _handoffCommands = new(StringComparer.Ordinal);
    private string? _pairingCode;
    private DateTimeOffset? _pairingCodeExpiresAt;

    public BrowserBridgeService(ApplicationState state, ProjectService projectService)
    {
        _state = state;
        _projectService = projectService;
        _state.BrowserBridge ??= new BrowserBridgeState();
        _state.BrowserBridge.PairedInstallationIds ??= [];
        _state.BrowserBridge.Bindings ??= [];
        _state.BrowserBridge.Tasks ??= [];
    }

    public event EventHandler? Changed;

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
            await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
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

            binding.ChatGPTUrl = chatGPTUrl;
            binding.ChatGPTConversationId = conversationId;
            binding.PageTitle = pageTitle ?? string.Empty;
            binding.LastSeenAt = DateTimeOffset.UtcNow;
            binding.Connected = true;
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

            var previousAssignments = _state.BrowserBridge.Bindings
                .Select(item => (Binding: item, WorkstreamId: item.WorkstreamId))
                .ToList();
            foreach (var other in _state.BrowserBridge.Bindings.Where(item => item.WorkstreamId == request.WorkstreamId && !ReferenceEquals(item, binding)))
            {
                other.WorkstreamId = null;
            }

            binding.WorkstreamId = workstream.Id;
            binding.LastSeenAt = DateTimeOffset.UtcNow;
            binding.Connected = true;
            var saveResult = await _projectService.TrySaveAsync(cancellationToken).ConfigureAwait(false);
            if (!saveResult.Success)
            {
                foreach (var previous in previousAssignments)
                {
                    previous.Binding.WorkstreamId = previous.WorkstreamId;
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

    public async Task<BridgeOperationResult> UnbindWorkstreamAsync(
        Guid workstreamId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = false;
            foreach (var binding in _state.BrowserBridge.Bindings.Where(item => item.WorkstreamId == workstreamId))
            {
                binding.WorkstreamId = null;
                changed = true;
            }

            if (!changed)
            {
                return new BridgeOperationResult(true);
            }

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
            var previousTaskId = workstream.CurrentTaskId;
            _state.BrowserBridge.Tasks.Add(task);
            workstream.CurrentTaskId = task.Id.ToString("D");

            var stateResult = await _projectService.TryChangeStateAsync(
                workstream.ProjectId,
                workstream.Id,
                WorkflowState.ReadyForCodex,
                manualOverride: false,
                cancellationToken).ConfigureAwait(false);
            if (!stateResult.Success)
            {
                workstream.CurrentTaskId = previousTaskId;
                _state.BrowserBridge.Tasks.Remove(task);
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

            var previousResult = task.Result;
            var previousStatus = task.Status;
            task.Result = result.Trim();
            task.Status = RelayTaskStatus.ReadyForChatGPT;
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

            if (string.IsNullOrWhiteSpace(task.Result))
            {
                return Failure<HandoffCommand>("result_missing", "There is no result to send back to ChatGPT.");
            }

            var binding = _state.BrowserBridge.Bindings.FirstOrDefault(item => item.TabKey == task.SourceTabKey);
            if (binding is null || binding.WorkstreamId != task.WorkstreamId)
            {
                return Failure<HandoffCommand>("tab_not_bound", "The original ChatGPT tab is no longer bound to this Workstream.");
            }

            if (!IsConnected(binding))
            {
                return Failure<HandoffCommand>("tab_disconnected", "The original ChatGPT tab is disconnected. Keep the result and reconnect the tab.");
            }

            var command = new HandoffCommand(
                Guid.NewGuid(),
                task.Id,
                task.WorkstreamId,
                binding.TabId,
                binding.ChatGPTUrl,
                task.Result);
            _handoffCommands[binding.TabKey] = command;
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

            return _handoffCommands.TryGetValue(binding.TabKey, out var command)
                ? new BridgeOperationResult<HandoffCommand>(true, command)
                : new BridgeOperationResult<HandoffCommand>(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BridgeOperationResult<RelayTask>> AcknowledgeHandoffAsync(
        Guid commandId,
        bool success,
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

            if (!success)
            {
                _handoffCommands.Remove(commandEntry.Key);
                return new BridgeOperationResult<RelayTask>(true, task);
            }

            var previousStatus = task.Status;
            task.Status = RelayTaskStatus.ChatGPTReviewing;
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
        return await ChangeTaskStateAsync(taskId, WorkflowState.Completed, RelayTaskStatus.Completed, cancellationToken).ConfigureAwait(false);
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
            IsConnected(binding));
    }

    private static BridgeOperationResult Failure(string code, string message) => new(false, code, message);

    private static BridgeOperationResult<T> Failure<T>(string code, string message) => new(false, default, code, message);
}
