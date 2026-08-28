using MediaBrush = System.Windows.Media.Brush;
using BlueRelay.Localization;
using BlueRelay.Models;
using BlueRelay.Services.Bridges;
using Wpf.Ui.Controls;
using MediaBrushes = System.Windows.Media.Brushes;

namespace BlueRelay.Presentation.ViewModels;

public sealed class ProjectListItemViewModel : ObservableObject
{
    private readonly UiTextSet _text;
    private readonly BrowserBridgeService? _browserBridge;
    private bool _isRefreshing;
    private WorkflowState _debugState;

    public ProjectListItemViewModel(Project project, Workstream workstream, UiTextSet text)
        : this(project, workstream, text, null)
    {
    }

    public ProjectListItemViewModel(Project project, Workstream workstream, UiTextSet text, BrowserBridgeService? browserBridge)
    {
        Project = project;
        Workstream = workstream;
        _text = text;
        _browserBridge = browserBridge;
        _debugState = workstream.CurrentState;
    }

    public Project Project { get; }

    public Workstream Workstream { get; }

    public Guid Id => Workstream.Id;

    public Guid ProjectId => Project.Id;

    public string ProjectName => Project.Name;

    public string WorkstreamName => string.Equals(Workstream.Name, BlueRelay.Models.Workstream.DefaultName, StringComparison.Ordinal)
        ? _text.DefaultWorkstream
        : Workstream.Name;

    // Name remains a compact binding-friendly alias for the second visual line.
    public string Name => WorkstreamName;

    public string LocalPath => Project.LocalPath;

    public string? CurrentTaskId => Guid.TryParse(Workstream.CurrentTaskId, out var taskId)
        ? taskId.ToString("D")
        : null;

    public string CurrentTaskText => CurrentTask is null
        ? _text.CurrentTaskNone
        : CurrentTask.Prompt;

    public string CurrentTaskResult => CurrentTask?.Result ?? string.Empty;

    public string UserNote
    {
        get => CurrentTask?.UserNote ?? string.Empty;
        set
        {
            if (CurrentTask is { } task && !string.Equals(task.UserNote ?? string.Empty, value, StringComparison.Ordinal))
            {
                task.UserNote = value;
                OnPropertyChanged();
            }
        }
    }

    public string ResultNote
    {
        get => CurrentTask?.ResultNote ?? string.Empty;
        set
        {
            if (CurrentTask is { } task && !string.Equals(task.ResultNote ?? string.Empty, value, StringComparison.Ordinal))
            {
                task.ResultNote = value;
                OnPropertyChanged();
            }
        }
    }

    public string TaskPayloadInfo => CurrentTask?.Payload is { } payload
        ? $"task.md · {FormatLength(payload.Length)}"
        : string.Empty;

    public string ResultPayloadInfo => CurrentTask?.ResultPayload is { } payload
        ? $"result.md · {FormatLength(payload.Length)}"
        : string.Empty;

    public string CodexProgress => Workstream.CodexProgress ?? string.Empty;

    public bool HasCodexProgress => !string.IsNullOrWhiteSpace(CodexProgress);

    public bool CanCancelCodex => IsCodexRunning && CurrentTask is { CodexTurnId: not null };

    public RelayTask? CurrentTask => _browserBridge?.FindTaskByWorkstream(Workstream.Id);

    public bool HasCurrentTask => CurrentTask is not null;

    public bool HasCurrentResult => !string.IsNullOrWhiteSpace(CurrentTask?.Result);

    public bool CanSendToCodex =>
        CurrentState == WorkflowState.ReadyForCodex &&
        HasCurrentTask;

    public bool CanFillCodex =>
        CurrentState == WorkflowState.ReadyForCodex &&
        HasCurrentTask;

    public string SendToCodexLabel => _text.SendToCodex;

    public bool IsCodexRunning => CurrentState == WorkflowState.CodexRunning;

    public bool CanSendToChatGPT => CurrentState == WorkflowState.ReadyForChatGPT && HasCurrentResult && !IsDeliveryPending;

    public bool IsChatGPTReviewing => CurrentState == WorkflowState.ChatGPTReviewing;

    public RelayCommandDeliveryStatus DeliveryStatus => CurrentTask?.DeliveryStatus ?? RelayCommandDeliveryStatus.None;

    public string? DeliveryErrorCode => CurrentTask?.DeliveryErrorCode;

    public bool IsDeliveryPending => CurrentState == WorkflowState.ReadyForChatGPT &&
                                      DeliveryStatus is RelayCommandDeliveryStatus.Queued or RelayCommandDeliveryStatus.Delivering;

    public bool HasDeliveryError => CurrentState == WorkflowState.ReadyForChatGPT &&
                                    DeliveryStatus == RelayCommandDeliveryStatus.Failed;

    public string SendToChatGPTLabel => HasDeliveryError ? _text.RetrySendToChatGPT : _text.SendToChatGPT;

    public string BrowserConnectionText => _browserBridge?.FindBindingDto(Workstream.Id) is { } binding
        ? binding.Connected ? _text.BrowserConnected : _text.BrowserDisconnected
        : _text.BrowserNotBound;

    private BrowserBindingDto? Binding => _browserBridge?.FindBindingDto(Workstream.Id);

    public string ChatGPTPairingText => string.IsNullOrWhiteSpace(Workstream.ChatGPTTitle)
        ? _text.ChatGPTNotBound
        : Workstream.ChatGPTTitle;

    public string ChatGPTPairingStatus => Binding?.ConversationMismatch == true
        ? _text.ChatGPTConversationChanged
        : Binding?.Connected == true ? _text.BrowserConnected : _text.BrowserDisconnected;

    public string ChatGPTPairingTooltip => string.Join(Environment.NewLine, new[]
    {
        Workstream.ChatGPTTitle,
        Workstream.ChatGPTUrl,
        Workstream.ChatGPTConversationId is { Length: > 0 } conversation ? $"Conversation: {conversation}" : null,
        Workstream.ChatGPTTabId is { Length: > 0 } tab ? $"Tab: {tab}" : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string CodexPairingText => string.IsNullOrWhiteSpace(Workstream.CodexThreadId)
        ? _text.CodexNotBound
        : ShortIdentity(Workstream.CodexThreadId);

    public string CodexPairingStatus => Workstream.CodexErrorCode == "codex_thread_conflict"
        ? _text.CodexConflict
        : string.IsNullOrWhiteSpace(Workstream.CodexThreadId) ? _text.CodexNotBound : _text.CodexBound;

    public string CodexThreadIdTooltip => Workstream.CodexThreadId ?? _text.CodexNotBound;

    public bool HasCodexThreadConflict => Workstream.CodexErrorCode == "codex_thread_conflict";

    public string CodexIssueText => Workstream.CodexError ?? string.Empty;

    public MediaBrush BrowserConnectionBrush => _browserBridge?.FindBindingDto(Workstream.Id)?.Connected == true
        ? System.Windows.Media.Brushes.LightGreen
        : System.Windows.Media.Brushes.SlateGray;

    public WorkflowState CurrentState => Workstream.CurrentState;

    public WorkflowState DebugState
    {
        get => _debugState;
        set
        {
            if (!SetProperty(ref _debugState, value) || _isRefreshing)
            {
                return;
            }

            StateChangeRequested?.Invoke(this, value);
        }
    }

    public event EventHandler<WorkflowState>? StateChangeRequested;

    public string StatusLabel => WorkflowStateCatalog.Describe(CurrentState, _text).Label;

    public string StatusMarker => WorkflowStateCatalog.Describe(CurrentState, _text).Marker;

    public MediaBrush StatusBrush => WorkflowStateCatalog.Describe(CurrentState, _text).Brush;

    public SymbolRegular StatusIcon => CurrentState switch
    {
        WorkflowState.Idle => SymbolRegular.Circle12,
        WorkflowState.ReadyForCodex => SymbolRegular.ArrowRight16,
        WorkflowState.CodexRunning => SymbolRegular.PlayCircle16,
        WorkflowState.ReadyForChatGPT => SymbolRegular.ArrowLeft12,
        WorkflowState.ChatGPTReviewing => SymbolRegular.Chat16,
        WorkflowState.Completed => SymbolRegular.CheckmarkCircle12,
        WorkflowState.NeedsAttention => SymbolRegular.Warning16,
        WorkflowState.Error => SymbolRegular.ErrorCircle12,
        _ => SymbolRegular.Circle12
    };

    public string Guidance => IsDeliveryPending
        ? _text.HandoffQueued
        : HasDeliveryError
            ? _text.HandoffFailed
            : WorkflowStateCatalog.Describe(CurrentState, _text).Guidance;

    public MediaBrush GuidanceBrush => HasDeliveryError
        ? MediaBrushes.IndianRed
        : IsDeliveryPending
            ? MediaBrushes.Gold
            : MediaBrushes.LightGray;

    public string PendingLabel => PendingCount == 0 ? string.Empty : $"{_text.Pending}: {PendingCount}";

    public int PendingCount => CurrentState is WorkflowState.ReadyForCodex or WorkflowState.ReadyForChatGPT or WorkflowState.Error ? 1 : 0;

    public void Refresh()
    {
        _isRefreshing = true;
        try
        {
            DebugState = Workstream.CurrentState;
        }
        finally
        {
            _isRefreshing = false;
        }

        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WorkstreamName));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(LocalPath));
        OnPropertyChanged(nameof(CurrentTaskText));
        OnPropertyChanged(nameof(CurrentTaskId));
        OnPropertyChanged(nameof(CurrentTaskResult));
        OnPropertyChanged(nameof(UserNote));
        OnPropertyChanged(nameof(ResultNote));
        OnPropertyChanged(nameof(TaskPayloadInfo));
        OnPropertyChanged(nameof(ResultPayloadInfo));
        OnPropertyChanged(nameof(CodexProgress));
        OnPropertyChanged(nameof(HasCodexProgress));
        OnPropertyChanged(nameof(CanCancelCodex));
        OnPropertyChanged(nameof(CurrentTask));
        OnPropertyChanged(nameof(HasCurrentTask));
        OnPropertyChanged(nameof(HasCurrentResult));
        OnPropertyChanged(nameof(CanSendToCodex));
        OnPropertyChanged(nameof(CanFillCodex));
        OnPropertyChanged(nameof(SendToCodexLabel));
        OnPropertyChanged(nameof(IsCodexRunning));
        OnPropertyChanged(nameof(CanSendToChatGPT));
        OnPropertyChanged(nameof(IsChatGPTReviewing));
        OnPropertyChanged(nameof(DeliveryStatus));
        OnPropertyChanged(nameof(DeliveryErrorCode));
        OnPropertyChanged(nameof(IsDeliveryPending));
        OnPropertyChanged(nameof(HasDeliveryError));
        OnPropertyChanged(nameof(SendToChatGPTLabel));
        OnPropertyChanged(nameof(BrowserConnectionText));
        OnPropertyChanged(nameof(Binding));
        OnPropertyChanged(nameof(ChatGPTPairingText));
        OnPropertyChanged(nameof(ChatGPTPairingStatus));
        OnPropertyChanged(nameof(ChatGPTPairingTooltip));
        OnPropertyChanged(nameof(CodexPairingText));
        OnPropertyChanged(nameof(CodexPairingStatus));
        OnPropertyChanged(nameof(CodexThreadIdTooltip));
        OnPropertyChanged(nameof(HasCodexThreadConflict));
        OnPropertyChanged(nameof(CodexIssueText));
        OnPropertyChanged(nameof(BrowserConnectionBrush));
        OnPropertyChanged(nameof(CurrentState));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusMarker));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(Guidance));
        OnPropertyChanged(nameof(GuidanceBrush));
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(PendingCount));
    }

    private static string FormatLength(long length)
    {
        return length < 1024
            ? $"{length} B"
            : length < 1024 * 1024
                ? $"{length / 1024d:0.#} KB"
                : $"{length / 1024d / 1024d:0.#} MB";
    }

    private static string ShortIdentity(string value)
    {
        return value.Length <= 16 ? value : $"{value[..8]}…{value[^6..]}";
    }
}
