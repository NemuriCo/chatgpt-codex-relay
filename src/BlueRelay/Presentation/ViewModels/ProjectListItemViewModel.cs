using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using BlueRelay.Models;

namespace BlueRelay.Presentation.ViewModels;

public sealed class ProjectListItemViewModel : ObservableObject
{
    public ProjectListItemViewModel(Project project)
    {
        Project = project;
    }

    public Project Project { get; }

    public Guid Id => Project.Id;

    public string Name => Project.Name;

    public string LocalPath => Project.LocalPath;

    public string CurrentTaskText => string.IsNullOrWhiteSpace(Project.CurrentTaskId)
        ? "Not assigned"
        : Project.CurrentTaskId;

    public WorkflowState CurrentState => Project.CurrentState;

    public string StatusLabel => WorkflowStateCatalog.Describe(CurrentState).Label;

    public string StatusMarker => WorkflowStateCatalog.Describe(CurrentState).Marker;

    public MediaBrush StatusBrush => WorkflowStateCatalog.Describe(CurrentState).Brush;

    public string Guidance => WorkflowStateCatalog.Describe(CurrentState).Guidance;

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
    }
}
