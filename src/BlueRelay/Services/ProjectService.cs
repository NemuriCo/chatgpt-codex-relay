using BlueRelay.Models;
using BlueRelay.Persistence;

namespace BlueRelay.Services;

public sealed class ProjectService
{
    private readonly ApplicationState _state;
    private readonly IStateStore _stateStore;
    private readonly WorkflowStateMachine _stateMachine;

    public ProjectService(ApplicationState state, IStateStore stateStore, WorkflowStateMachine stateMachine)
    {
        _state = state;
        _stateStore = stateStore;
        _stateMachine = stateMachine;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Project> Projects => _state.Projects;

    public async Task<ProjectMutationResult> TryCreateAsync(
        string name,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        if (!ProjectValidator.TryValidate(name, localPath, _state.Projects, null, out var validName, out var validPath, out var error))
        {
            return new ProjectMutationResult(false, error);
        }

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = validName,
            LocalPath = validPath,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentState = WorkflowState.Idle
        };

        _state.Projects.Add(project);
        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            _state.Projects.Remove(project);
            return persistResult;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new ProjectMutationResult(true, string.Empty, project);
    }

    public async Task<ProjectMutationResult> TryUpdateAsync(
        Guid projectId,
        string name,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return new ProjectMutationResult(false, "The selected project no longer exists.");
        }

        if (!ProjectValidator.TryValidate(name, localPath, _state.Projects, projectId, out var validName, out var validPath, out var error))
        {
            return new ProjectMutationResult(false, error);
        }

        var originalName = project.Name;
        var originalPath = project.LocalPath;
        var originalUpdatedAt = project.UpdatedAt;
        project.Name = validName;
        project.LocalPath = validPath;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            project.Name = originalName;
            project.LocalPath = originalPath;
            project.UpdatedAt = originalUpdatedAt;
            return persistResult;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new ProjectMutationResult(true, string.Empty);
    }

    public async Task<ProjectMutationResult> TryDeleteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var index = _state.Projects.FindIndex(project => project.Id == projectId);
        if (index < 0)
        {
            return new ProjectMutationResult(false, "The selected project no longer exists.");
        }

        var project = _state.Projects[index];
        _state.Projects.RemoveAt(index);
        var originalSelection = _state.SelectedProjectId;
        if (_state.SelectedProjectId == projectId)
        {
            _state.SelectedProjectId = _state.Projects.FirstOrDefault()?.Id;
        }

        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            _state.Projects.Insert(index, project);
            _state.SelectedProjectId = originalSelection;
            return persistResult;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new ProjectMutationResult(true, string.Empty);
    }

    public async Task<ProjectMutationResult> TryChangeStateAsync(
        Guid projectId,
        WorkflowState target,
        bool manualOverride,
        CancellationToken cancellationToken = default)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return new ProjectMutationResult(false, "The selected project no longer exists.");
        }

        var originalState = project.CurrentState;
        var originalUpdatedAt = project.UpdatedAt;
        if (!_stateMachine.TryTransition(project, target, manualOverride, out var error))
        {
            return new ProjectMutationResult(false, error);
        }

        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            project.CurrentState = originalState;
            project.UpdatedAt = originalUpdatedAt;
            return persistResult;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new ProjectMutationResult(true, string.Empty);
    }

    public Task<ProjectMutationResult> TrySaveAsync(CancellationToken cancellationToken = default)
    {
        return TryPersistAsync(cancellationToken);
    }

    public Project? Find(Guid projectId)
    {
        return _state.Projects.FirstOrDefault(project => project.Id == projectId);
    }

    private async Task<ProjectMutationResult> TryPersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SaveAsync(_state, cancellationToken);
            return new ProjectMutationResult(true, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ProjectMutationResult(
                false,
                $"BlueRelay could not save its local state. Details: {exception.Message}");
        }
    }
}
