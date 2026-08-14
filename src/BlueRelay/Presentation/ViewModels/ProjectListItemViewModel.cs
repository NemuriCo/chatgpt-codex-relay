using MediaBrush = System.Windows.Media.Brush;
using BlueRelay.Localization;
using BlueRelay.Models;
using Wpf.Ui.Controls;

namespace BlueRelay.Presentation.ViewModels;

public sealed class ProjectListItemViewModel : ObservableObject
{
    private readonly UiTextSet _text;
    private bool _isRefreshing;
    private WorkflowState _debugState;

    public ProjectListItemViewModel(Project project, Workstream workstream, UiTextSet text)
    {
        Project = project;
        Workstream = workstream;
        _text = text;
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

    public string CurrentTaskText => string.IsNullOrWhiteSpace(Workstream.CurrentTaskId)
        ? _text.CurrentTaskNone
        : Workstream.CurrentTaskId;

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

    public string Guidance => WorkflowStateCatalog.Describe(CurrentState, _text).Guidance;

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
        OnPropertyChanged(nameof(CurrentState));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusMarker));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(Guidance));
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(PendingCount));
    }
}
