using System.Collections.ObjectModel;
using System.Windows.Input;
using BlueRelay.Models;
using BlueRelay.Services;
using BlueRelay.Services.Dialogs;

namespace BlueRelay.Presentation.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ApplicationState _state;
    private readonly ProjectService _projectService;
    private readonly IDialogService _dialogService;
    private readonly IFolderPicker _folderPicker;
    private readonly RelayCommand _editProjectCommand;
    private readonly RelayCommand _deleteProjectCommand;
    private readonly RelayCommand _applyStateCommand;
    private readonly RelayCommand _saveProjectCommand;
    private readonly RelayCommand _cancelEditCommand;
    private ProjectListItemViewModel? _selectedProject;
    private WorkflowState _manualState;
    private bool _isEditing;
    private bool _isCreating;
    private bool _isAlwaysOnTop;
    private string _editingName = string.Empty;
    private string _editingLocalPath = string.Empty;
    private string? _statusMessage;
    private bool _statusIsError;

    public MainViewModel(
        ApplicationState state,
        ProjectService projectService,
        IDialogService dialogService,
        IFolderPicker folderPicker,
        string? loadWarning)
    {
        _state = state;
        _projectService = projectService;
        _dialogService = dialogService;
        _folderPicker = folderPicker;
        _manualState = WorkflowState.Idle;
        _isAlwaysOnTop = state.IsAlwaysOnTop;
        StateOptions = WorkflowStateCatalog.AllStates
            .Select(stateValue => new StateOption(stateValue, WorkflowStateCatalog.Describe(stateValue).Label))
            .ToList();

        NewProjectCommand = new RelayCommand(StartNewProject);
        _editProjectCommand = new RelayCommand(StartEditProject, () => SelectedProject is not null && !IsEditing);
        _deleteProjectCommand = new RelayCommand(DeleteSelectedProject, () => SelectedProject is not null && !IsEditing);
        _applyStateCommand = new RelayCommand(ApplyManualState, () => SelectedProject is not null && !IsEditing);
        _saveProjectCommand = new RelayCommand(SaveProject, () => IsEditing);
        _cancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        BrowsePathCommand = new RelayCommand(BrowseForProjectDirectory, () => IsEditing);
        ToggleAlwaysOnTopCommand = new RelayCommand(ToggleAlwaysOnTop);

        _projectService.Changed += ProjectService_Changed;
        RefreshProjects(_state.SelectedProjectId);
        if (!string.IsNullOrWhiteSpace(loadWarning))
        {
            SetStatus(loadWarning, isError: true);
        }
    }

    public ObservableCollection<ProjectListItemViewModel> Projects { get; } = [];

    public IReadOnlyList<StateOption> StateOptions { get; }

    public ICommand NewProjectCommand { get; }

    public ICommand EditProjectCommand => _editProjectCommand;

    public ICommand DeleteProjectCommand => _deleteProjectCommand;

    public ICommand ApplyStateCommand => _applyStateCommand;

    public ICommand SaveProjectCommand => _saveProjectCommand;

    public ICommand CancelEditCommand => _cancelEditCommand;

    public ICommand BrowsePathCommand { get; }

    public ICommand ToggleAlwaysOnTopCommand { get; }

    public ProjectListItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value) || value is null)
            {
                if (value is null)
                {
                    _state.SelectedProjectId = null;
                    _projectService.TrySave(out _);
                }

                RaiseCommandStates();
                return;
            }

            _state.SelectedProjectId = value.Id;
            if (!_projectService.TrySave(out var error))
            {
                SetStatus(error, isError: true);
            }

            ManualState = value.CurrentState;
            RaiseCommandStates();
        }
    }

    public WorkflowState ManualState
    {
        get => _manualState;
        set => SetProperty(ref _manualState, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(HasDetails));
                OnPropertyChanged(nameof(IsProjectListEnabled));
                RaiseCommandStates();
            }
        }
    }

    public bool HasDetails => IsEditing || SelectedProject is not null;

    public bool HasProjects => Projects.Count > 0;

    public bool IsProjectListEnabled => !IsEditing;

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        private set => SetProperty(ref _isAlwaysOnTop, value);
    }

    public string EditingName
    {
        get => _editingName;
        set => SetProperty(ref _editingName, value);
    }

    public string EditingLocalPath
    {
        get => _editingLocalPath;
        set => SetProperty(ref _editingLocalPath, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public void ToggleAlwaysOnTop()
    {
        IsAlwaysOnTop = !IsAlwaysOnTop;
        _state.IsAlwaysOnTop = IsAlwaysOnTop;
        if (!_projectService.TrySave(out var error))
        {
            SetStatus(error, isError: true);
            return;
        }

        SetStatus(IsAlwaysOnTop ? "Always on top enabled." : "Always on top disabled.");
    }

    private void StartNewProject()
    {
        _isCreating = true;
        SelectedProject = null;
        EditingName = string.Empty;
        EditingLocalPath = string.Empty;
        ManualState = WorkflowState.Idle;
        IsEditing = true;
        SetStatus("Create a project record. BlueRelay will not change files in the selected directory.");
    }

    private void StartEditProject()
    {
        if (SelectedProject is null)
        {
            return;
        }

        _isCreating = false;
        EditingName = SelectedProject.Name;
        EditingLocalPath = SelectedProject.LocalPath;
        ManualState = SelectedProject.CurrentState;
        IsEditing = true;
        SetStatus("Edit the project record, then save your changes.");
    }

    private void SaveProject()
    {
        if (_isCreating)
        {
            if (!_projectService.TryCreate(EditingName, EditingLocalPath, out var createdProject, out var createError))
            {
                SetStatus(createError, isError: true);
                return;
            }

            IsEditing = false;
            _isCreating = false;
            RefreshProjects(createdProject!.Id);
            SetStatus("Project created.");
            return;
        }

        if (SelectedProject is null)
        {
            return;
        }

        if (!_projectService.TryUpdate(SelectedProject.Id, EditingName, EditingLocalPath, out var updateError))
        {
            SetStatus(updateError, isError: true);
            return;
        }

        var selectedId = SelectedProject.Id;
        IsEditing = false;
        RefreshProjects(selectedId);
        SetStatus("Project updated.");
    }

    private void CancelEdit()
    {
        _isCreating = false;
        IsEditing = false;
        RefreshProjects(_state.SelectedProjectId);
        SetStatus("Changes discarded.");
    }

    private void BrowseForProjectDirectory()
    {
        var selectedPath = _folderPicker.Pick(EditingLocalPath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            EditingLocalPath = selectedPath;
        }
    }

    private void DeleteSelectedProject()
    {
        if (SelectedProject is null)
        {
            return;
        }

        var project = SelectedProject.Project;
        var confirmed = _dialogService.Confirm(
            "Delete project record?",
            $"Remove '{project.Name}' from BlueRelay?\n\nThe local directory will not be deleted.");
        if (!confirmed)
        {
            return;
        }

        if (!_projectService.TryDelete(project.Id, out var error))
        {
            SetStatus(error, isError: true);
            return;
        }

        RefreshProjects(_state.SelectedProjectId);
        SetStatus("Project record deleted. No local files were changed.");
    }

    private void ApplyManualState()
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (!_projectService.TryChangeState(SelectedProject.Id, ManualState, manualOverride: true, out var error))
        {
            SetStatus(error, isError: true);
            return;
        }

        RefreshProjects(SelectedProject.Id);
        SetStatus("Workflow state updated manually.");
    }

    private void ProjectService_Changed(object? sender, EventArgs e)
    {
        // All project records are refreshed together so each project retains its own state.
        RefreshProjects(_state.SelectedProjectId);
    }

    private void RefreshProjects(Guid? preferredProjectId)
    {
        var selectedId = preferredProjectId;
        Projects.Clear();
        foreach (var project in _state.Projects.OrderByDescending(project => project.UpdatedAt))
        {
            Projects.Add(new ProjectListItemViewModel(project));
        }

        OnPropertyChanged(nameof(HasProjects));
        var nextSelection = selectedId.HasValue
            ? Projects.FirstOrDefault(project => project.Id == selectedId.Value)
            : null;
        nextSelection ??= Projects.FirstOrDefault();
        if (!ReferenceEquals(_selectedProject, nextSelection))
        {
            SelectedProject = nextSelection;
        }
        else if (nextSelection is not null)
        {
            nextSelection.Refresh();
        }

        RaiseCommandStates();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }

    private void RaiseCommandStates()
    {
        _editProjectCommand.RaiseCanExecuteChanged();
        _deleteProjectCommand.RaiseCanExecuteChanged();
        _applyStateCommand.RaiseCanExecuteChanged();
        _saveProjectCommand.RaiseCanExecuteChanged();
        _cancelEditCommand.RaiseCanExecuteChanged();
        if (BrowsePathCommand is RelayCommand browseCommand)
        {
            browseCommand.RaiseCanExecuteChanged();
        }
    }
}
