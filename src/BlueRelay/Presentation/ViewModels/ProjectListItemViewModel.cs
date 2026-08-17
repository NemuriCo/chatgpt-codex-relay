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

    public RelayTask? CurrentTask => _browserBridge?.FindTaskByWorkstream(Workstream.Id);

    public bool HasCurrentTask => CurrentTask is not null;

    public bool HasCurrentResult => !string.IsNullOrWhiteSpace(CurrentTask?.Result);

    public bool CanSendToCodex => CurrentState == WorkflowState.ReadyForCodex && HasCurrentTask;

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
        OnPropertyChanged(nameof(CurrentTask));
        OnPropertyChanged(nameof(HasCurrentTask));
        OnPropertyChanged(nameof(HasCurrentResult));
        OnPropertyChanged(nameof(CanSendToCodex));
        OnPropertyChanged(nameof(IsCodexRunning));
        OnPropertyChanged(nameof(CanSendToChatGPT));
        OnPropertyChanged(nameof(IsChatGPTReviewing));
        OnPropertyChanged(nameof(DeliveryStatus));
        OnPropertyChanged(nameof(DeliveryErrorCode));
        OnPropertyChanged(nameof(IsDeliveryPending));
        OnPropertyChanged(nameof(HasDeliveryError));
        OnPropertyChanged(nameof(SendToChatGPTLabel));
        OnPropertyChanged(nameof(BrowserConnectionText));
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
}
