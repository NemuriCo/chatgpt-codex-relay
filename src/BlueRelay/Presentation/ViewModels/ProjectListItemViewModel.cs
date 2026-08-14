using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using BlueRelay.Localization;
using BlueRelay.Models;

namespace BlueRelay.Presentation.ViewModels;

public sealed class ProjectListItemViewModel : ObservableObject
{
    private readonly UiTextSet _text;

    public ProjectListItemViewModel(Project project)
        : this(project, LocalizationService.Current)
    {
    }

    public ProjectListItemViewModel(Project project, UiTextSet text)
    {
        Project = project;
        _text = text;
    }

    public Project Project { get; }

    public Guid Id => Project.Id;

    public string Name => Project.Name;

    public string LocalPath => Project.LocalPath;

    public string CurrentTaskText => string.IsNullOrWhiteSpace(Project.CurrentTaskId)
        ? _text.CurrentTaskNone
        : Project.CurrentTaskId;

    public WorkflowState CurrentState => Project.CurrentState;

    public string StatusLabel => WorkflowStateCatalog.Describe(CurrentState, _text).Label;

    public string StatusMarker => WorkflowStateCatalog.Describe(CurrentState, _text).Marker;

    public MediaBrush StatusBrush => WorkflowStateCatalog.Describe(CurrentState, _text).Brush;

    public string Guidance => WorkflowStateCatalog.Describe(CurrentState, _text).Guidance;

    public string PendingLabel => PendingCount == 0 ? string.Empty : $"{_text.Pending}: {PendingCount}";

    public int PendingCount => CurrentState is WorkflowState.ReadyForCodex or WorkflowState.ReadyForChatGPT or WorkflowState.Error ? 1 : 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(LocalPath));
        OnPropertyChanged(nameof(CurrentTaskText));
        OnPropertyChanged(nameof(CurrentState));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusMarker));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(Guidance));
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(PendingCount));
    }
}
