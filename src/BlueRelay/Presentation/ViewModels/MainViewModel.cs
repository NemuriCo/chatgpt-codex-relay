using System.Collections.ObjectModel;
using System.Windows.Input;
using BlueRelay.Localization;
using BlueRelay.Models;
using BlueRelay.Services;
using BlueRelay.Services.Bridges;
using BlueRelay.Services.Dialogs;
using BlueRelay.Services.Codex;
using System.Windows;
using WpfApplication = System.Windows.Application;
using Wpf.Ui.Controls;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace BlueRelay.Presentation.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ApplicationState _state;
    private readonly ProjectService _projectService;
    private readonly IDialogService _dialogService;
    private readonly IFolderPicker _folderPicker;
    private readonly IGitRepositoryDetector _gitRepositoryDetector;
    private readonly BrowserBridgeService _browserBridge;
    private readonly ICodexBridge? _codexBridge;
    private readonly RelayCommand _editProjectCommand;
    private readonly RelayCommand _deleteProjectCommand;
    private readonly RelayCommand _saveProjectCommand;
    private readonly RelayCommand _cancelEditCommand;
    private readonly RelayCommand _renameWorkstreamCommand;
    private readonly RelayCommand _deleteWorkstreamCommand;
    private readonly RelayCommand _saveWorkstreamCommand;
    private readonly RelayCommand _cancelWorkstreamEditCommand;
    private readonly RelayCommand _refreshProjectGitCommand;
    private readonly RelayCommand _selectProjectCommand;
    private readonly RelayCommand _selectWorkstreamCommand;
    private readonly RelayCommand _confirmTaskCommand;
    private readonly RelayCommand _cancelCodexTaskCommand;
    private readonly RelayCommand _resetCodexThreadCommand;
    private readonly RelayCommand _newCodexSessionAndRetryCommand;
    private readonly RelayCommand _copyCodexThreadIdCommand;
    private readonly RelayCommand _handoffResultCommand;
    private readonly RelayCommand _completeCurrentRoundCommand;
    private readonly RelayCommand _clearCurrentTaskCommand;
    private readonly RelayCommand _openDebugCommand;
    private readonly RelayCommand _openSimulatedResultCommand;
    private readonly RelayCommand _simulateResultCommand;
    private readonly RelayCommand _cancelDebugCommand;
    private readonly RelayCommand _manualStateCommand;
    private readonly RelayCommand _generatePairingCodeCommand;
    private readonly RelayCommand _resetPairingCommand;
    private readonly RelayCommand _unbindWorkstreamCommand;
    private readonly RelayCommand _openTaskDetailCommand;
    private readonly RelayCommand _closeTaskDetailCommand;
    private Project? _selectedProject;
    private ProjectListItemViewModel? _selectedWorkstream;
    private bool _isEditing;
    private bool _isCreating;
    private bool _isAlwaysOnTop;
    private bool _isProjectManagementOpen;
    private bool _isEditingWorkstream;
    private bool _isCreatingWorkstream;
    private bool _isDetectingGit;
    private bool _useRepositoryRoot;
    private bool _nameManuallyEdited;
    private bool _applyingRepositorySuggestion;
    private string _editingName = string.Empty;
    private string _editingLocalPath = string.Empty;
    private string _editingWorkstreamName = string.Empty;
    private string _lastSuggestedName = string.Empty;
    private string? _statusMessage;
    private bool _statusIsError;
    private GitRepositoryInfo? _repositoryInfo;
    private bool _browserBridgeAvailable;
    private string? _browserBridgeStatus;
    private PairingCodeInfo _pairingCode = new(null, null);
    private ProjectListItemViewModel? _debugWorkstream;
    private bool _isDebugOpen;
    private bool _isSimulatingResult;
    private bool _isTaskDetailOpen;
    private ProjectListItemViewModel? _taskDetailWorkstream;
    private string _simulatedResultText = string.Empty;
    private WorkflowState _manualState;

    public MainViewModel(
        ApplicationState state,
        ProjectService projectService,
        IDialogService dialogService,
        IFolderPicker folderPicker,
        string? loadWarning)
        : this(state, projectService, dialogService, folderPicker, new GitRepositoryDetector(), loadWarning, null, null)
    {
    }

    public MainViewModel(
        ApplicationState state,
        ProjectService projectService,
        IDialogService dialogService,
        IFolderPicker folderPicker,
        IGitRepositoryDetector gitRepositoryDetector,
        string? loadWarning,
        BrowserBridgeService? browserBridge = null,
        ICodexBridge? codexBridge = null)
    {
        _state = state;
        _projectService = projectService;
        _dialogService = dialogService;
        _folderPicker = folderPicker;
        _gitRepositoryDetector = gitRepositoryDetector;
        _browserBridge = browserBridge ?? new BrowserBridgeService(state, projectService);
        _codexBridge = codexBridge ?? _browserBridge.CodexBridge;
        Ui = LocalizationService.Current;
        _isAlwaysOnTop = state.IsAlwaysOnTop;

        StateOptions = WorkflowStateCatalog.AllStates
            .Select(stateValue => new StateOption(stateValue, Ui.GetStateLabel(stateValue)))
            .ToList();

        NewProjectCommand = new RelayCommand(StartNewProject);
        OpenProjectManagementCommand = new RelayCommand(OpenProjectManagement);
        CloseProjectManagementCommand = new RelayCommand(CloseProjectManagement);
        ToggleCollapsedCommand = new RelayCommand(ToggleCollapsed);
        _selectProjectCommand = new RelayCommand(SelectProject);
        _selectWorkstreamCommand = new RelayCommand(SelectWorkstream);
        _confirmTaskCommand = new RelayCommand(ConfirmTaskAsync, CanRunTaskAction);
        _cancelCodexTaskCommand = new RelayCommand(CancelCodexTaskAsync, CanCancelCodexTask);
        _resetCodexThreadCommand = new RelayCommand(ResetCodexThreadAsync, CanSelectWorkstream);
        _newCodexSessionAndRetryCommand = new RelayCommand(NewCodexSessionAndRetryAsync, CanSelectWorkstream);
        _copyCodexThreadIdCommand = new RelayCommand(CopyCodexThreadId, CanCopyCodexThreadId);
        _handoffResultCommand = new RelayCommand(HandoffResultAsync, CanHandoffResult);
        _completeCurrentRoundCommand = new RelayCommand(CompleteCurrentRoundAsync, CanCompleteCurrentRound);
        _clearCurrentTaskCommand = new RelayCommand(ClearCurrentTaskAsync, CanSelectWorkstream);
        _openDebugCommand = new RelayCommand(OpenDebug, CanSelectWorkstream);
        _openSimulatedResultCommand = new RelayCommand(OpenSimulatedResult, CanSimulateResult);
        _simulateResultCommand = new RelayCommand(SimulateResultAsync, () => IsSimulatingResult && !string.IsNullOrWhiteSpace(SimulatedResultText));
        _cancelDebugCommand = new RelayCommand(CloseDebug);
        _manualStateCommand = new RelayCommand(ApplyManualStateAsync, () => IsDebugOpen && DebugWorkstream is not null);
        _generatePairingCodeCommand = new RelayCommand(GeneratePairingCode);
        _resetPairingCommand = new RelayCommand(ResetPairingAsync, () => true);
        _unbindWorkstreamCommand = new RelayCommand(UnbindWorkstreamAsync, CanSelectWorkstream);
        _openTaskDetailCommand = new RelayCommand(OpenTaskDetail);
        _closeTaskDetailCommand = new RelayCommand(CloseTaskDetail);
        NewWorkstreamCommand = new RelayCommand(StartNewWorkstream, () => SelectedProject is not null && !IsEditing && !IsEditingWorkstream);
        _editProjectCommand = new RelayCommand(StartEditProject, CanMutateProject);
        _deleteProjectCommand = new RelayCommand(DeleteSelectedProjectAsync, CanMutateProject);
        _saveProjectCommand = new RelayCommand(SaveProjectAsync, () => IsEditing);
        _cancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        _renameWorkstreamCommand = new RelayCommand(StartRenameWorkstream, CanMutateWorkstream);
        _deleteWorkstreamCommand = new RelayCommand(DeleteSelectedWorkstreamAsync, CanMutateWorkstream);
        _saveWorkstreamCommand = new RelayCommand(SaveWorkstreamAsync, () => IsEditingWorkstream);
        _cancelWorkstreamEditCommand = new RelayCommand(CancelWorkstreamEdit, () => IsEditingWorkstream);
        BrowsePathCommand = new RelayCommand(BrowseForProjectDirectoryAsync, () => IsEditing && !IsDetectingGit);
        RefreshRepositoryCommand = new RelayCommand(RefreshRepositoryAsync, () => IsEditing && !IsDetectingGit && !string.IsNullOrWhiteSpace(EditingLocalPath));
        _refreshProjectGitCommand = new RelayCommand(RefreshProjectGitAsync, CanMutateProject);
        ToggleAlwaysOnTopCommand = new RelayCommand(ToggleAlwaysOnTopAsync, () => !IsEditing && !IsEditingWorkstream);

        _projectService.Changed += ProjectService_Changed;
        _browserBridge.Changed += BrowserBridge_Changed;
        if (_codexBridge is not null)
        {
            _codexBridge.ProgressChanged += CodexBridge_ProgressChanged;
            _codexBridge.ApprovalRequested += CodexBridge_ApprovalRequested;
            _codexBridge.StatusChanged += CodexBridge_StatusChanged;
            _codexBridge.DiagnosticsChanged += CodexBridge_DiagnosticsChanged;
        }
        if (_browserBridge.PairingCode.Code is null)
        {
            _pairingCode = _browserBridge.GeneratePairingCode();
        }
        RefreshData(_state.SelectedProjectId, null);
        if (!string.IsNullOrWhiteSpace(loadWarning))
        {
            SetStatus(loadWarning, isError: true);
        }
    }

    public UiTextSet Ui { get; }

    public ObservableCollection<Project> Projects { get; } = [];

    public ObservableCollection<ProjectListItemViewModel> Workstreams { get; } = [];

    public ObservableCollection<ProjectListItemViewModel> SelectedProjectWorkstreams { get; } = [];

    public IReadOnlyList<StateOption> StateOptions { get; }

    public ICommand NewProjectCommand { get; }

    public ICommand OpenProjectManagementCommand { get; }

    public ICommand CloseProjectManagementCommand { get; }

    public ICommand ToggleCollapsedCommand { get; }

    public ICommand SelectProjectCommand => _selectProjectCommand;

    public ICommand SelectWorkstreamCommand => _selectWorkstreamCommand;

    public ICommand NewWorkstreamCommand { get; }

    public ICommand EditProjectCommand => _editProjectCommand;

    public ICommand DeleteProjectCommand => _deleteProjectCommand;

    public ICommand SaveProjectCommand => _saveProjectCommand;

    public ICommand CancelEditCommand => _cancelEditCommand;

    public ICommand RenameWorkstreamCommand => _renameWorkstreamCommand;

    public ICommand DeleteWorkstreamCommand => _deleteWorkstreamCommand;

    public ICommand SaveWorkstreamCommand => _saveWorkstreamCommand;

    public ICommand CancelWorkstreamEditCommand => _cancelWorkstreamEditCommand;

    public ICommand BrowsePathCommand { get; }

    public ICommand RefreshRepositoryCommand { get; }

    public ICommand RefreshProjectGitCommand => _refreshProjectGitCommand;

    public ICommand ToggleAlwaysOnTopCommand { get; }

    public ICommand ConfirmTaskCommand => _confirmTaskCommand;

    public ICommand CancelCodexTaskCommand => _cancelCodexTaskCommand;

    public ICommand ResetCodexThreadCommand => _resetCodexThreadCommand;

    public ICommand NewCodexSessionAndRetryCommand => _newCodexSessionAndRetryCommand;

    public ICommand CopyCodexThreadIdCommand => _copyCodexThreadIdCommand;

    public CodexBridgeStatus CodexStatus => _codexBridge?.Status ?? CodexBridgeStatus.Disconnected;

    public string CodexStatusText => CodexStatus switch
    {
        CodexBridgeStatus.Connected => Ui.BrowserBridgeRunning,
        CodexBridgeStatus.Running => Ui.CodexRunning,
        CodexBridgeStatus.WaitingForApproval => Ui.CodexApprovalTitle,
        CodexBridgeStatus.Connecting => Ui.CodexRunning,
        CodexBridgeStatus.Error => string.IsNullOrWhiteSpace(CodexErrorText)
            ? Ui.GetStateLabel(WorkflowState.Error)
            : CodexErrorText,
        _ => Ui.NoSession
    };

    public string CodexVersion => _codexBridge?.Version ?? string.Empty;

    public string CodexErrorText => _codexBridge?.ErrorMessage ?? string.Empty;

    public bool HasCodexError => !string.IsNullOrWhiteSpace(CodexErrorText);

    public string CodexDiagnosticsText => _codexBridge?.Diagnostics.ToDisplayText() ?? string.Empty;

    public ICommand HandoffResultCommand => _handoffResultCommand;

    public ICommand CompleteCurrentRoundCommand => _completeCurrentRoundCommand;

    public ICommand ClearCurrentTaskCommand => _clearCurrentTaskCommand;

    public ICommand OpenDebugCommand => _openDebugCommand;

    public ICommand OpenSimulatedResultCommand => _openSimulatedResultCommand;

    public ICommand SimulateResultCommand => _simulateResultCommand;

    public ICommand CancelDebugCommand => _cancelDebugCommand;

    public ICommand ManualStateCommand => _manualStateCommand;

    public ICommand GeneratePairingCodeCommand => _generatePairingCodeCommand;

    public ICommand ResetPairingCommand => _resetPairingCommand;

    public ICommand UnbindWorkstreamCommand => _unbindWorkstreamCommand;

    public ICommand OpenTaskDetailCommand => _openTaskDetailCommand;

    public ICommand CloseTaskDetailCommand => _closeTaskDetailCommand;

    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            _state.SelectedProjectId = value?.Id;
            _ = PersistSelectionAsync();
            OnPropertyChanged(nameof(HasDetails));
            RefreshWorkstreams(null);
            RaiseCommandStates();
        }
    }

    public ProjectListItemViewModel? SelectedWorkstream
    {
        get => _selectedWorkstream;
        set
        {
            if (value is not null && !ReferenceEquals(SelectedProject, value.Project))
            {
                var workstreamId = value.Id;
                SelectedProject = value.Project;
                value = Workstreams.FirstOrDefault(workstream => workstream.Id == workstreamId);
            }

            if (!SetProperty(ref _selectedWorkstream, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedWorkstreamModel));

            RaiseCommandStates();
        }
    }

    public Workstream? SelectedWorkstreamModel
    {
        get => SelectedWorkstream?.Workstream;
        set
        {
            var item = value is null
                ? null
                : Workstreams.FirstOrDefault(workstream => ReferenceEquals(workstream.Workstream, value) || workstream.Id == value.Id);
            SelectedWorkstream = item;
        }
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

    public bool IsEditingWorkstream
    {
        get => _isEditingWorkstream;
        private set
        {
            if (SetProperty(ref _isEditingWorkstream, value))
            {
                OnPropertyChanged(nameof(IsProjectListEnabled));
                RaiseCommandStates();
            }
        }
    }

    public bool IsProjectManagementOpen
    {
        get => _isProjectManagementOpen;
        private set => SetProperty(ref _isProjectManagementOpen, value);
    }

    public bool IsBrowserBridgeAvailable
    {
        get => _browserBridgeAvailable;
        private set
        {
            if (SetProperty(ref _browserBridgeAvailable, value))
            {
                OnPropertyChanged(nameof(BrowserBridgeStatusBrush));
            }
        }
    }

    public string BrowserBridgeStatus => _browserBridgeStatus
        ?? (IsBrowserBridgeAvailable ? Ui.BrowserBridgeRunning : Ui.BrowserBridgeUnavailable);

    public MediaBrush BrowserBridgeStatusBrush => IsBrowserBridgeAvailable ? MediaBrushes.LightGreen : MediaBrushes.IndianRed;

    public string BrowserBridgeEndpoint => $"127.0.0.1:{Services.Bridges.BrowserBridgeService.DefaultPort}";

    public string PairingCode => _pairingCode.Code ?? Ui.PairingCodeNotGenerated;

    public string PairingCodeExpiry => _pairingCode.ExpiresAt is { } expiry
        ? expiry.ToLocalTime().ToString("HH:mm:ss")
        : string.Empty;

    public ProjectListItemViewModel? DebugWorkstream
    {
        get => _debugWorkstream;
        private set
        {
            if (SetProperty(ref _debugWorkstream, value))
            {
                ManualState = value?.CurrentState ?? WorkflowState.Idle;
                OnPropertyChanged(nameof(HasDebugWorkstream));
            }
        }
    }

    public bool HasDebugWorkstream => DebugWorkstream is not null;

    public bool IsDebugOpen
    {
        get => _isDebugOpen;
        private set => SetProperty(ref _isDebugOpen, value);
    }

    public bool IsSimulatingResult
    {
        get => _isSimulatingResult;
        private set => SetProperty(ref _isSimulatingResult, value);
    }

    public string SimulatedResultText
    {
        get => _simulatedResultText;
        set
        {
            if (SetProperty(ref _simulatedResultText, value))
            {
                _simulateResultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public WorkflowState ManualState
    {
        get => _manualState;
        set
        {
            if (SetProperty(ref _manualState, value))
            {
                _manualStateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDetectingGit
    {
        get => _isDetectingGit;
        private set
        {
            if (SetProperty(ref _isDetectingGit, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public GitRepositoryInfo? RepositoryInfo
    {
        get => _repositoryInfo;
        private set
        {
            if (SetProperty(ref _repositoryInfo, value))
            {
                OnPropertyChanged(nameof(HasRepositoryInfo));
                OnPropertyChanged(nameof(HasAlternativeRepositoryRoot));
                OnPropertyChanged(nameof(HasRepositoryRoot));
                OnPropertyChanged(nameof(GitInfoTitle));
                OnPropertyChanged(nameof(GitInfoMessage));
                OnPropertyChanged(nameof(SelectedFolderText));
                OnPropertyChanged(nameof(RepositoryRootText));
            }
        }
    }

    public bool HasRepositoryInfo => RepositoryInfo is not null;

    public bool HasAlternativeRepositoryRoot => RepositoryInfo?.IsGitRepository == true &&
                                                !PathsEqual(RepositoryInfo.SelectedPath, RepositoryInfo.RepositoryRoot);

    public bool HasRepositoryRoot => !string.IsNullOrWhiteSpace(RepositoryInfo?.RepositoryRoot);

    public string GitInfoTitle => RepositoryInfo is null
        ? string.Empty
        : RepositoryInfo.IsGitRepository
            ? Ui.GitDetected
            : RepositoryInfo.GitAvailable ? Ui.GitNotDetected : Ui.GitUnavailable;

    public string GitInfoMessage => RepositoryInfo is null
        ? string.Empty
        : RepositoryInfo.IsGitRepository
            ? Ui.GitRepositoryHint
            : RepositoryInfo.GitAvailable ? Ui.GitNotRepositoryHint : Ui.GitUnavailableHint;

    public string RepositoryRootText => RepositoryInfo?.RepositoryRoot ?? string.Empty;

    public string SelectedFolderText => RepositoryInfo?.SelectedPath ?? string.Empty;

    public bool UseRepositoryRoot
    {
        get => _useRepositoryRoot;
        set
        {
            if (!SetProperty(ref _useRepositoryRoot, value) || RepositoryInfo?.RepositoryRoot is null)
            {
                return;
            }

            EditingLocalPath = value ? RepositoryInfo.RepositoryRoot : RepositoryInfo.SelectedPath;
        }
    }

    public bool IsCollapsed => _state.IsWindowCollapsed;

    public double? WindowLeft => _state.WindowLeft;

    public double? WindowTop => _state.WindowTop;

    public double? WindowWidth => _state.WindowWidth;

    public double? WindowHeight => _state.WindowHeight;

    public string CollapseGlyph => IsCollapsed ? "⌄" : "⌃";

    public string CollapseToolTip => IsCollapsed ? Ui.Expand : Ui.Collapse;

    public string AlwaysOnTopToolTip => IsAlwaysOnTop ? Ui.AlwaysOnTopEnabled : Ui.AlwaysOnTopDisabled;

    public ControlAppearance AlwaysOnTopAppearance => IsAlwaysOnTop ? ControlAppearance.Primary : ControlAppearance.Secondary;

    public bool HasDetails => IsEditing || SelectedProject is not null;

    public bool IsTaskDetailOpen
    {
        get => _isTaskDetailOpen;
        private set => SetProperty(ref _isTaskDetailOpen, value);
    }

    public ProjectListItemViewModel? TaskDetailWorkstream
    {
        get => _taskDetailWorkstream;
        private set
        {
            if (SetProperty(ref _taskDetailWorkstream, value))
            {
                OnPropertyChanged(nameof(TaskDetailPrompt));
                OnPropertyChanged(nameof(TaskDetailResult));
                OnPropertyChanged(nameof(HasTaskDetailResult));
            }
        }
    }

    public string TaskDetailPrompt => TaskDetailWorkstream?.CurrentTaskText ?? string.Empty;

    public string TaskDetailResult => TaskDetailWorkstream?.CurrentTaskResult ?? string.Empty;

    public bool HasTaskDetailResult => TaskDetailWorkstream?.HasCurrentResult == true;

    public bool HasProjects => Projects.Count > 0;

    public bool HasWorkstreams => Workstreams.Count > 0;

    public bool IsProjectListEnabled => !IsEditing && !IsEditingWorkstream;

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        private set
        {
            if (SetProperty(ref _isAlwaysOnTop, value))
            {
                OnPropertyChanged(nameof(AlwaysOnTopToolTip));
                OnPropertyChanged(nameof(AlwaysOnTopAppearance));
            }
        }
    }

    public string EditingName
    {
        get => _editingName;
        set
        {
            if (SetProperty(ref _editingName, value) && !_applyingRepositorySuggestion)
            {
                _nameManuallyEdited = true;
            }
        }
    }

    public string EditingLocalPath
    {
        get => _editingLocalPath;
        set => SetProperty(ref _editingLocalPath, value);
    }

    public string EditingWorkstreamName
    {
        get => _editingWorkstreamName;
        set => SetProperty(ref _editingWorkstreamName, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
                OnPropertyChanged(nameof(HasErrorStatus));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasErrorStatus => HasStatusMessage && StatusIsError;

    public MediaBrush StatusAccentBrush => StatusIsError ? MediaBrushes.IndianRed : MediaBrushes.LightSkyBlue;

    public bool StatusIsError
    {
        get => _statusIsError;
        private set
        {
            if (SetProperty(ref _statusIsError, value))
            {
                OnPropertyChanged(nameof(HasErrorStatus));
                OnPropertyChanged(nameof(StatusAccentBrush));
            }
        }
    }

    public async Task ToggleAlwaysOnTopAsync()
    {
        IsAlwaysOnTop = !IsAlwaysOnTop;
        _state.IsAlwaysOnTop = IsAlwaysOnTop;
        var saveResult = await _projectService.TrySaveAsync();
        if (!saveResult.Success)
        {
            SetStatus(saveResult.Error, isError: true);
        }
    }

    public void UpdateWindowPosition(double left, double top)
    {
        _state.WindowLeft = left;
        _state.WindowTop = top;
        OnPropertyChanged(nameof(WindowLeft));
        OnPropertyChanged(nameof(WindowTop));
    }

    public void UpdateWindowSize(double width, double height)
    {
        if (IsCollapsed)
        {
            return;
        }

        _state.WindowWidth = width;
        _state.WindowHeight = height;
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
    }

    public async Task SaveWindowSettingsAsync()
    {
        var saveResult = await _projectService.TrySaveAsync();
        if (!saveResult.Success)
        {
            SetStatus(saveResult.Error, isError: true);
        }
    }

    public Task StopCodexAsync()
    {
        return _codexBridge?.StopAsync() ?? Task.CompletedTask;
    }

    public void SetBrowserBridgeStatus(bool isAvailable, string? status)
    {
        IsBrowserBridgeAvailable = isAvailable;
        _browserBridgeStatus = string.IsNullOrWhiteSpace(status) ? null : status;
        OnPropertyChanged(nameof(BrowserBridgeStatus));
    }

    private void ToggleCollapsed()
    {
        _state.IsWindowCollapsed = !_state.IsWindowCollapsed;
        if (_state.IsWindowCollapsed)
        {
            CloseTaskDetail();
        }
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(CollapseGlyph));
        OnPropertyChanged(nameof(CollapseToolTip));
        _ = SaveWindowSettingsAsync();
    }

    private void OpenProjectManagement()
    {
        CloseTaskDetail();
        if (IsCollapsed)
        {
            _state.IsWindowCollapsed = false;
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(CollapseGlyph));
            OnPropertyChanged(nameof(CollapseToolTip));
            _ = SaveWindowSettingsAsync();
        }

        IsProjectManagementOpen = true;
    }

    private void OpenTaskDetail(object? parameter)
    {
        if (parameter is not ProjectListItemViewModel workstream || !workstream.HasCurrentTask)
        {
            return;
        }

        TaskDetailWorkstream = workstream;
        IsTaskDetailOpen = true;
    }

    private void CloseTaskDetail()
    {
        IsTaskDetailOpen = false;
        TaskDetailWorkstream = null;
    }

    private void CloseProjectManagement()
    {
        if (IsEditing)
        {
            CancelEdit();
        }

        if (IsEditingWorkstream)
        {
            CancelWorkstreamEdit();
        }

        CloseDebug();

        IsProjectManagementOpen = false;
    }

    private void SelectProject(object? parameter)
    {
        if (parameter is Project projectParameter)
        {
            SelectedProject = projectParameter;
        }
    }

    private void SelectWorkstream(object? parameter)
    {
        if (parameter is ProjectListItemViewModel workstreamParameter)
        {
            SelectedWorkstream = workstreamParameter;
        }
    }

    private bool CanMutateProject(object? parameter)
    {
        return !IsEditing && !IsEditingWorkstream && (parameter is Project || SelectedProject is not null);
    }

    private bool CanMutateWorkstream(object? parameter)
    {
        return !IsEditing && !IsEditingWorkstream && (parameter is ProjectListItemViewModel || SelectedWorkstream is not null);
    }

    private void StartNewProject()
    {
        _isCreating = true;
        SelectedProject = null;
        SelectedWorkstream = null;
        SetEditingName(string.Empty, manuallyEdited: false);
        EditingLocalPath = string.Empty;
        ClearRepositoryInfo();
        IsProjectManagementOpen = true;
        IsEditing = true;
        SetStatus(Ui.CreateProjectHint);
    }

    private void StartEditProject(object? parameter = null)
    {
        if (parameter is Project project)
        {
            SelectedProject = project;
        }

        if (SelectedProject is null)
        {
            return;
        }

        _isCreating = false;
        SetEditingName(SelectedProject.Name, manuallyEdited: true);
        EditingLocalPath = SelectedProject.LocalPath;
        ClearRepositoryInfo();
        IsProjectManagementOpen = true;
        IsEditing = true;
        SetStatus(Ui.EditProjectHint);
    }

    private void StartNewWorkstream()
    {
        if (SelectedProject is null)
        {
            return;
        }

        _isCreatingWorkstream = true;
        EditingWorkstreamName = Ui.NewWorkstream;
        IsEditingWorkstream = true;
        RaiseCommandStates();
    }

    private void StartRenameWorkstream(object? parameter = null)
    {
        if (parameter is ProjectListItemViewModel workstream)
        {
            SelectedWorkstream = workstream;
        }

        if (SelectedWorkstream is null)
        {
            return;
        }

        _isCreatingWorkstream = false;
        EditingWorkstreamName = SelectedWorkstream.Workstream.Name;
        IsEditingWorkstream = true;
    }

    private async Task SaveProjectAsync()
    {
        if (_isCreating)
        {
            var createResult = await _projectService.TryCreateAsync(EditingName, EditingLocalPath);
            if (!createResult.Success)
            {
                SetStatus(createResult.Error, isError: true);
                return;
            }

            IsEditing = false;
            _isCreating = false;
            RefreshData(createResult.Project!.Id, createResult.Project.Workstreams[0].Id);
            SetStatus(Ui.ProjectCreated);
            return;
        }

        if (SelectedProject is null)
        {
            return;
        }

        var updateResult = await _projectService.TryUpdateAsync(SelectedProject.Id, EditingName, EditingLocalPath);
        if (!updateResult.Success)
        {
            SetStatus(updateResult.Error, isError: true);
            return;
        }

        var selectedProjectId = SelectedProject.Id;
        var selectedWorkstreamId = SelectedWorkstream?.Id;
        IsEditing = false;
        RefreshData(selectedProjectId, selectedWorkstreamId);
        SetStatus(Ui.ProjectUpdated);
    }

    private void CancelEdit()
    {
        _isCreating = false;
        IsEditing = false;
        ClearRepositoryInfo();
        RefreshData(_state.SelectedProjectId, SelectedWorkstream?.Id);
        SetStatus(Ui.ChangesDiscarded);
    }

    private async Task BrowseForProjectDirectoryAsync()
    {
        var selectedPath = _folderPicker.Pick(EditingLocalPath);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        EditingLocalPath = selectedPath;
        await DetectGitAsync(selectedPath);
    }

    private Task RefreshRepositoryAsync()
    {
        return DetectGitAsync(EditingLocalPath);
    }

    private async Task RefreshProjectGitAsync(object? parameter)
    {
        if (parameter is Project project)
        {
            SelectedProject = project;
        }

        if (SelectedProject is null)
        {
            return;
        }

        StartEditProject();
        await DetectGitAsync(EditingLocalPath);
    }

    private async Task DetectGitAsync(string selectedPath)
    {
        IsDetectingGit = true;
        try
        {
            var info = await _gitRepositoryDetector.DetectAsync(selectedPath);
            RepositoryInfo = info;
            _useRepositoryRoot = info.IsGitRepository && HasAlternativeRoot(info);
            OnPropertyChanged(nameof(UseRepositoryRoot));
            SetEditingNameFromSuggestion(info.SuggestedName);
            EditingLocalPath = _useRepositoryRoot && info.RepositoryRoot is not null
                ? info.RepositoryRoot
                : info.SelectedPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ClearRepositoryInfo();
            SetStatus($"{Ui.GitUnavailable}: {exception.Message}", isError: true);
        }
        finally
        {
            IsDetectingGit = false;
        }
    }

    private async Task DeleteSelectedProjectAsync(object? parameter = null)
    {
        if (parameter is Project projectParameter)
        {
            SelectedProject = projectParameter;
        }

        if (SelectedProject is null)
        {
            return;
        }

        var project = SelectedProject;
        var confirmed = await _dialogService.ConfirmAsync(
            Ui.DeleteProjectTitle,
            string.Format(Ui.DeleteProjectMessageFormat, project.Name));
        if (!confirmed)
        {
            return;
        }

        var deleteResult = await _projectService.TryDeleteAsync(project.Id);
        if (!deleteResult.Success)
        {
            SetStatus(deleteResult.Error, isError: true);
            return;
        }

        RefreshData(_state.SelectedProjectId, null);
        SetStatus(Ui.ProjectDeleted);
    }

    private async Task SaveWorkstreamAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        var isCreating = _isCreatingWorkstream;
        WorkstreamMutationResult result;
        if (isCreating)
        {
            result = await _projectService.TryCreateWorkstreamAsync(SelectedProject.Id, EditingWorkstreamName);
        }
        else if (SelectedWorkstream is not null)
        {
            result = await _projectService.TryRenameWorkstreamAsync(
                SelectedProject.Id,
                SelectedWorkstream.Id,
                EditingWorkstreamName);
        }
        else
        {
            return;
        }

        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        var workstreamId = result.Workstream?.Id;
        IsEditingWorkstream = false;
        _isCreatingWorkstream = false;
        RefreshData(SelectedProject.Id, workstreamId);
        SetStatus(isCreating ? Ui.WorkstreamCreated : Ui.WorkstreamUpdated);
    }

    private void CancelWorkstreamEdit()
    {
        _isCreatingWorkstream = false;
        IsEditingWorkstream = false;
        SetStatus(Ui.ChangesDiscarded);
    }

    private async Task DeleteSelectedWorkstreamAsync(object? parameter = null)
    {
        if (parameter is ProjectListItemViewModel workstreamParameter)
        {
            SelectedWorkstream = workstreamParameter;
        }

        if (SelectedProject is null || SelectedWorkstream is null)
        {
            return;
        }

        if (SelectedProject.Workstreams.Count <= 1)
        {
            SetStatus(Ui.LastWorkstreamRequired, isError: true);
            return;
        }

        var workstream = SelectedWorkstream.Workstream;
        var confirmed = await _dialogService.ConfirmAsync(
            Ui.DeleteWorkstreamTitle,
            string.Format(Ui.DeleteWorkstreamMessageFormat, workstream.Name));
        if (!confirmed)
        {
            return;
        }

        var deleteResult = await _projectService.TryDeleteWorkstreamAsync(SelectedProject.Id, workstream.Id);
        if (!deleteResult.Success)
        {
            SetStatus(deleteResult.Error, isError: true);
            return;
        }

        RefreshData(SelectedProject.Id, null);
        SetStatus(Ui.WorkstreamDeleted);
    }

    private void ProjectService_Changed(object? sender, EventArgs e)
    {
        RefreshDataOnUiThread(_state.SelectedProjectId, SelectedWorkstream?.Id);
    }

    private void BrowserBridge_Changed(object? sender, EventArgs e)
    {
        RefreshDataOnUiThread(
            _state.SelectedProjectId,
            SelectedWorkstream?.Id,
        UpdateDeliveryStatus);
    }

    private void CodexBridge_StatusChanged(object? sender, EventArgs e)
    {
        RefreshDataOnUiThread(_state.SelectedProjectId, SelectedWorkstream?.Id, () =>
        {
            OnPropertyChanged(nameof(CodexStatus));
            OnPropertyChanged(nameof(CodexStatusText));
            OnPropertyChanged(nameof(CodexVersion));
            OnPropertyChanged(nameof(CodexErrorText));
            OnPropertyChanged(nameof(HasCodexError));
            OnPropertyChanged(nameof(CodexDiagnosticsText));
            RaiseCommandStates();
        });
    }

    private void CodexBridge_DiagnosticsChanged(object? sender, EventArgs e)
    {
        RefreshDataOnUiThread(_state.SelectedProjectId, SelectedWorkstream?.Id, () =>
        {
            OnPropertyChanged(nameof(CodexStatusText));
            OnPropertyChanged(nameof(CodexErrorText));
            OnPropertyChanged(nameof(HasCodexError));
            OnPropertyChanged(nameof(CodexDiagnosticsText));
        });
    }

    private void CodexBridge_ProgressChanged(object? sender, CodexProgressUpdate update)
    {
        var workstream = _projectService.FindWorkstreamForId(update.WorkstreamId);
        if (workstream is null)
        {
            return;
        }

        workstream.CodexProgress = update.Stage + "：" + update.Detail;
        RefreshDataOnUiThread(_state.SelectedProjectId, update.WorkstreamId);
    }

    private async void CodexBridge_ApprovalRequested(object? sender, CodexApprovalRequest request)
    {
        try
        {
            var message = BuildApprovalMessage(request);
            var approved = await AskOnUiThreadAsync(Ui.CodexApprovalTitle, message, Ui.Allow).ConfigureAwait(false);
            if (_codexBridge is not null)
            {
                await _codexBridge.RespondToApprovalAsync(
                    request.RequestId,
                    request.Method,
                    approved,
                    request.Params).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private Task<bool> AskOnUiThreadAsync(string title, string message, string acceptLabel)
    {
        if (WpfApplication.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
        {
            return _dialogService.AskAsync(title, message, acceptLabel);
        }

        return dispatcher.InvokeAsync(
                () => _dialogService.AskAsync(title, message, acceptLabel))
            .Task
            .Unwrap();
    }

    private static string BuildApprovalMessage(CodexApprovalRequest request)
    {
        var summary = request.Method switch
        {
            "item/commandExecution/requestApproval" => ReadJsonString(request.Params, "command") ?? "Codex wants to execute a command.",
            "item/fileChange/requestApproval" => ReadJsonString(request.Params, "reason") ?? "Codex wants to modify project files.",
            "item/permissions/requestApproval" => ReadJsonString(request.Params, "reason") ?? "Codex wants additional project permissions.",
            "item/tool/requestUserInput" => "Codex is asking for user input.",
            _ => "Codex sent an approval request."
        };
        return summary + "\n\n" + LocalizationService.Current.CodexApprovalMessage;
    }

    private static string? ReadJsonString(System.Text.Json.JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var property in path)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object || !current.TryGetProperty(property, out current))
            {
                return null;
            }
        }

        return current.ValueKind == System.Text.Json.JsonValueKind.String ? current.GetString() : null;
    }

    private void RefreshDataOnUiThread(
        Guid? preferredProjectId,
        Guid? preferredWorkstreamId,
        Action? afterRefresh = null)
    {
        if (WpfApplication.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                RefreshData(preferredProjectId, preferredWorkstreamId);
                afterRefresh?.Invoke();
            });
            return;
        }

        RefreshData(preferredProjectId, preferredWorkstreamId);
        afterRefresh?.Invoke();
    }

    private void UpdateDeliveryStatus()
    {
        var workstream = SelectedWorkstream;
        if (workstream?.IsDeliveryPending == true)
        {
            SetStatus(Ui.HandoffQueued);
        }
        else if (workstream?.HasDeliveryError == true)
        {
            SetStatus(
                workstream.DeliveryErrorCode == "clipboard_fallback"
                    ? Ui.HandoffFallback
                    : Ui.HandoffFailed,
                isError: true);
        }
        else if (workstream?.CurrentState == WorkflowState.ChatGPTReviewing &&
                 workstream.DeliveryStatus == RelayCommandDeliveryStatus.Delivered)
        {
            SetStatus(Ui.HandoffDelivered);
        }
    }

    private void RefreshData(Guid? preferredProjectId, Guid? preferredWorkstreamId)
    {
        Projects.Clear();
        foreach (var project in _state.Projects.OrderByDescending(project => project.UpdatedAt))
        {
            Projects.Add(project);
        }

        OnPropertyChanged(nameof(HasProjects));
        var nextProject = preferredProjectId.HasValue
            ? Projects.FirstOrDefault(project => project.Id == preferredProjectId.Value)
            : null;
        nextProject ??= Projects.FirstOrDefault();
        if (!ReferenceEquals(_selectedProject, nextProject))
        {
            SelectedProject = nextProject;
        }
        else
        {
            RefreshWorkstreams(preferredWorkstreamId);
        }
    }

    private void RefreshWorkstreams(Guid? preferredWorkstreamId)
    {
        Workstreams.Clear();
        SelectedProjectWorkstreams.Clear();
        foreach (var project in _state.Projects)
        {
            foreach (var workstream in project.Workstreams.OrderByDescending(workstream => workstream.UpdatedAt))
            {
                var item = new ProjectListItemViewModel(project, workstream, Ui, _browserBridge);
                item.StateChangeRequested += Workstream_StateChangeRequested;
                Workstreams.Add(item);
                if (SelectedProject is not null && project.Id == SelectedProject.Id)
                {
                    SelectedProjectWorkstreams.Add(item);
                }
            }
        }

        OnPropertyChanged(nameof(HasWorkstreams));
        var nextWorkstream = preferredWorkstreamId.HasValue
            ? Workstreams.FirstOrDefault(workstream => workstream.Id == preferredWorkstreamId.Value)
            : SelectedProjectWorkstreams.FirstOrDefault();
        nextWorkstream ??= Workstreams.FirstOrDefault();
        if (!ReferenceEquals(_selectedWorkstream, nextWorkstream))
        {
            SelectedWorkstream = nextWorkstream;
        }
        else if (nextWorkstream is not null)
        {
            nextWorkstream.Refresh();
        }

        RefreshTaskDetailWorkstream();
        RaiseCommandStates();
    }

    private void RefreshTaskDetailWorkstream()
    {
        if (!IsTaskDetailOpen)
        {
            return;
        }

        var workstreamId = TaskDetailWorkstream?.Id;
        var refreshed = workstreamId.HasValue
            ? Workstreams.FirstOrDefault(workstream => workstream.Id == workstreamId.Value)
            : null;
        if (refreshed is null)
        {
            CloseTaskDetail();
            return;
        }

        TaskDetailWorkstream = refreshed;
    }

    private async void Workstream_StateChangeRequested(object? sender, WorkflowState target)
    {
        if (sender is ProjectListItemViewModel item)
        {
            await ApplyWorkstreamStateAsync(item, target);
        }
    }

    private async Task ApplyWorkstreamStateAsync(ProjectListItemViewModel item, WorkflowState target)
    {
        var originalState = item.CurrentState;
        if (originalState == target)
        {
            return;
        }

        var result = await _projectService.TryChangeStateAsync(
            item.ProjectId,
            item.Id,
            target,
            manualOverride: true);
        if (!result.Success)
        {
            item.Refresh();
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(item.ProjectId, item.Id);
        SetStatus(Ui.WorkflowUpdated);
    }

    private bool CanSelectWorkstream(object? parameter)
    {
        return parameter is ProjectListItemViewModel || SelectedWorkstream is not null;
    }

    private bool CanCopyCodexThreadId(object? parameter)
    {
        return (parameter as ProjectListItemViewModel ?? SelectedWorkstream)?.Workstream.CodexThreadId is { Length: > 0 };
    }

    private bool CanRunTaskAction(object? parameter)
    {
        return parameter is ProjectListItemViewModel item && item.CanSendToCodex;
    }

    private bool CanCancelCodexTask(object? parameter)
    {
        return parameter is ProjectListItemViewModel item && item.CanCancelCodex;
    }

    private bool CanHandoffResult(object? parameter)
    {
        return parameter is ProjectListItemViewModel item && item.CanSendToChatGPT;
    }

    private bool CanCompleteCurrentRound(object? parameter)
    {
        return parameter is ProjectListItemViewModel item
            && item.HasCurrentTask
            && item.CurrentState != WorkflowState.Completed;
    }

    private bool CanSimulateResult(object? parameter)
    {
        return parameter is ProjectListItemViewModel item
            ? item.IsCodexRunning && item.CurrentTask is not null
            : DebugWorkstream?.IsCodexRunning == true && DebugWorkstream.CurrentTask is not null;
    }

    private async Task ConfirmTaskAsync(object? parameter)
    {
        if (parameter is not ProjectListItemViewModel item || item.CurrentTask is not { } task)
        {
            return;
        }

        var result = _codexBridge is null
            ? await _browserBridge.ConfirmTaskAsync(task.Id)
            : await _browserBridge.SendTaskToCodexAsync(task.Id, item.UserNote);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(item.ProjectId, item.Id);
        SetStatus(_codexBridge is null ? Ui.CodexSimulationStarted : Ui.CodexRunning);
    }

    private async Task CancelCodexTaskAsync(object? parameter)
    {
        if (parameter is not ProjectListItemViewModel item)
        {
            return;
        }

        var result = await _browserBridge.CancelCodexTaskAsync(item.Id);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus(Ui.CodexCancelled);
    }

    private async Task ResetCodexThreadAsync(object? parameter)
    {
        var item = parameter as ProjectListItemViewModel ?? SelectedWorkstream;
        if (item is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(Ui.ResetCodexThreadTitle, Ui.ResetCodexThreadMessage);
        if (!confirmed)
        {
            return;
        }

        var result = await _browserBridge.ResetCodexThreadAsync(item.Id);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(item.ProjectId, item.Id);
        SetStatus(Ui.CodexThreadReset);
    }

    private async Task NewCodexSessionAndRetryAsync(object? parameter)
    {
        var item = parameter as ProjectListItemViewModel ?? SelectedWorkstream;
        if (item is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(Ui.NewCodexSessionAndRetryTitle, Ui.NewCodexSessionAndRetryMessage);
        if (!confirmed)
        {
            return;
        }

        var result = await _browserBridge.NewCodexSessionAndRetryAsync(item.Id, item.UserNote);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(item.ProjectId, item.Id);
        SetStatus(Ui.CodexSessionRetried);
    }

    private void CopyCodexThreadId(object? parameter)
    {
        var item = parameter as ProjectListItemViewModel ?? SelectedWorkstream;
        var threadId = item?.Workstream.CodexThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(threadId);
            SetStatus(Ui.CodexThreadIdCopied);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async Task HandoffResultAsync(object? parameter)
    {
        if (parameter is not ProjectListItemViewModel item || item.CurrentTask is not { } task)
        {
            return;
        }

        var result = await _browserBridge.QueueHandoffAsync(task.Id);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus(Ui.HandoffQueued);
    }

    private async Task CompleteCurrentRoundAsync(object? parameter)
    {
        if (parameter is not ProjectListItemViewModel item || item.CurrentTask is not { } task)
        {
            return;
        }

        var result = await _browserBridge.CompleteTaskAsync(task.Id);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(item.ProjectId, item.Id);
        SetStatus(Ui.CurrentRoundCompleted);
    }

    private async Task ClearCurrentTaskAsync(object? parameter)
    {
        var item = parameter as ProjectListItemViewModel ?? SelectedWorkstream;
        if (item is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            Ui.ClearCurrentTaskTitle,
            Ui.ClearCurrentTaskMessage);
        if (!confirmed)
        {
            return;
        }

        var result = await _browserBridge.ClearCurrentTaskAsync(item.Id);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        CloseTaskDetail();
        CloseDebug();
        RefreshData(item.ProjectId, item.Id);
        SetStatus(Ui.CurrentTaskCleared);
    }

    private void OpenDebug(object? parameter)
    {
        if (parameter is ProjectListItemViewModel item)
        {
            SelectedWorkstream = item;
        }

        DebugWorkstream = SelectedWorkstream;
        IsProjectManagementOpen = true;
        IsDebugOpen = true;
        IsSimulatingResult = false;
        SimulatedResultText = string.Empty;
    }

    private void OpenSimulatedResult(object? parameter)
    {
        if (!CanSimulateResult(parameter))
        {
            return;
        }

        if (parameter is ProjectListItemViewModel item)
        {
            SelectedWorkstream = item;
        }

        DebugWorkstream = SelectedWorkstream;
        IsProjectManagementOpen = true;
        IsDebugOpen = true;
        IsSimulatingResult = true;
        SimulatedResultText = string.Empty;
        OnPropertyChanged(nameof(IsSimulatingResult));
    }

    private void CloseDebug()
    {
        IsDebugOpen = false;
        IsSimulatingResult = false;
        SimulatedResultText = string.Empty;
    }

    private async Task SimulateResultAsync()
    {
        if (DebugWorkstream?.CurrentTask is not { } task || string.IsNullOrWhiteSpace(SimulatedResultText))
        {
            return;
        }

        var result = await _browserBridge.SimulateResultAsync(task.Id, SimulatedResultText);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        var projectId = DebugWorkstream.ProjectId;
        var workstreamId = DebugWorkstream.Id;
        CloseDebug();
        RefreshData(projectId, workstreamId);
        SetStatus(Ui.SimulatedResultSaved);
    }

    private async Task ApplyManualStateAsync()
    {
        if (DebugWorkstream is null)
        {
            return;
        }

        var result = await _projectService.TryChangeStateAsync(
            DebugWorkstream.ProjectId,
            DebugWorkstream.Id,
            ManualState,
            manualOverride: true);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(DebugWorkstream.ProjectId, DebugWorkstream.Id);
        SetStatus(Ui.WorkflowUpdated);
    }

    private void GeneratePairingCode()
    {
        _pairingCode = _browserBridge.GeneratePairingCode();
        OnPropertyChanged(nameof(PairingCode));
        OnPropertyChanged(nameof(PairingCodeExpiry));
    }

    private async Task ResetPairingAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(Ui.ResetPairingTitle, Ui.ResetPairingMessage);
        if (!confirmed)
        {
            return;
        }

        var result = await _browserBridge.ResetPairingAsync();
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        _pairingCode = _browserBridge.PairingCode;
        OnPropertyChanged(nameof(PairingCode));
        OnPropertyChanged(nameof(PairingCodeExpiry));
        SetStatus(Ui.PairingReset);
    }

    private async Task UnbindWorkstreamAsync(object? parameter)
    {
        var item = parameter as ProjectListItemViewModel ?? SelectedWorkstream;
        if (item is null)
        {
            return;
        }

        var result = await _browserBridge.UnbindWorkstreamAsync(item.Id);
        if (!result.Success)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        RefreshData(item.ProjectId, item.Id);
        SetStatus(Ui.WorkstreamUnbound);
    }

    private void SetEditingName(string value, bool manuallyEdited)
    {
        _nameManuallyEdited = manuallyEdited;
        _applyingRepositorySuggestion = true;
        try
        {
            EditingName = value;
        }
        finally
        {
            _applyingRepositorySuggestion = false;
        }
    }

    private void SetEditingNameFromSuggestion(string suggestedName)
    {
        _lastSuggestedName = suggestedName;
        if (_nameManuallyEdited && !string.IsNullOrWhiteSpace(EditingName))
        {
            return;
        }

        SetEditingName(suggestedName, manuallyEdited: false);
    }

    private void ClearRepositoryInfo()
    {
        RepositoryInfo = null;
        _useRepositoryRoot = false;
        _lastSuggestedName = string.Empty;
        OnPropertyChanged(nameof(UseRepositoryRoot));
    }

    private static bool HasAlternativeRoot(GitRepositoryInfo info)
    {
        return info.RepositoryRoot is not null && !PathsEqual(info.SelectedPath, info.RepositoryRoot);
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusIsError = isError;
        StatusMessage = message;
    }

    private async Task PersistSelectionAsync()
    {
        var saveResult = await _projectService.TrySaveAsync();
        if (!saveResult.Success)
        {
            SetStatus(saveResult.Error, isError: true);
        }
    }

    private void RaiseCommandStates()
    {
        _editProjectCommand.RaiseCanExecuteChanged();
        _deleteProjectCommand.RaiseCanExecuteChanged();
        _saveProjectCommand.RaiseCanExecuteChanged();
        _cancelEditCommand.RaiseCanExecuteChanged();
        _renameWorkstreamCommand.RaiseCanExecuteChanged();
        _deleteWorkstreamCommand.RaiseCanExecuteChanged();
        _saveWorkstreamCommand.RaiseCanExecuteChanged();
        _cancelWorkstreamEditCommand.RaiseCanExecuteChanged();
        _confirmTaskCommand.RaiseCanExecuteChanged();
        _cancelCodexTaskCommand.RaiseCanExecuteChanged();
        _resetCodexThreadCommand.RaiseCanExecuteChanged();
        _newCodexSessionAndRetryCommand.RaiseCanExecuteChanged();
        _copyCodexThreadIdCommand.RaiseCanExecuteChanged();
        _handoffResultCommand.RaiseCanExecuteChanged();
        _completeCurrentRoundCommand.RaiseCanExecuteChanged();
        _clearCurrentTaskCommand.RaiseCanExecuteChanged();
        _openDebugCommand.RaiseCanExecuteChanged();
        _openSimulatedResultCommand.RaiseCanExecuteChanged();
        _simulateResultCommand.RaiseCanExecuteChanged();
        _manualStateCommand.RaiseCanExecuteChanged();
        _unbindWorkstreamCommand.RaiseCanExecuteChanged();
        if (BrowsePathCommand is RelayCommand browseCommand)
        {
            browseCommand.RaiseCanExecuteChanged();
        }

        if (RefreshRepositoryCommand is RelayCommand refreshCommand)
        {
            refreshCommand.RaiseCanExecuteChanged();
        }

        _refreshProjectGitCommand.RaiseCanExecuteChanged();

        if (NewWorkstreamCommand is RelayCommand newWorkstreamCommand)
        {
            newWorkstreamCommand.RaiseCanExecuteChanged();
        }
    }
}
