using BlueRelay.Models;
using BlueRelay.Persistence;

namespace BlueRelay.Services;

public sealed class ProjectService
{
    private readonly ApplicationState _state;
    private readonly IStateStore _stateStore;
    private readonly WorkflowStateMachine _stateMachine;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ProjectService(ApplicationState state, IStateStore stateStore, WorkflowStateMachine stateMachine)
    {
        _state = state;
        _stateStore = stateStore;
        _stateMachine = stateMachine;
        StateMigration.Migrate(_state);
    }

    public event EventHandler? Changed;

    public string StateFilePath => _stateStore.FilePath;

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
        var projectId = Guid.NewGuid();
        var project = new Project
        {
            Id = projectId,
            Name = validName,
            LocalPath = validPath,
            CreatedAt = now,
            UpdatedAt = now,
            Workstreams =
            [
                new Workstream
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    Name = Workstream.DefaultName,
                    CurrentState = WorkflowState.Idle,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            ]
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

    public async Task<WorkstreamMutationResult> TryCreateWorkstreamAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return new WorkstreamMutationResult(false, "The selected project no longer exists.");
        }

        if (!WorkstreamValidator.TryValidate(name, project.Workstreams, null, out var validName, out var error))
        {
            return new WorkstreamMutationResult(false, error);
        }

        var now = DateTimeOffset.UtcNow;
        var workstream = new Workstream
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = validName,
            CreatedAt = now,
            UpdatedAt = now
        };
        project.Workstreams.Add(workstream);
        var originalUpdatedAt = project.UpdatedAt;
        project.UpdatedAt = now;

        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            project.Workstreams.Remove(workstream);
            project.UpdatedAt = originalUpdatedAt;
            return new WorkstreamMutationResult(false, persistResult.Error);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new WorkstreamMutationResult(true, string.Empty, workstream);
    }

    public async Task<WorkstreamMutationResult> TryRenameWorkstreamAsync(
        Guid projectId,
        Guid workstreamId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var project = Find(projectId);
        var workstream = project?.Workstreams.FirstOrDefault(item => item.Id == workstreamId);
        if (project is null || workstream is null)
        {
            return new WorkstreamMutationResult(false, "The selected workstream no longer exists.");
        }

        if (!WorkstreamValidator.TryValidate(name, project.Workstreams, workstreamId, out var validName, out var error))
        {
            return new WorkstreamMutationResult(false, error);
        }

        var originalName = workstream.Name;
        var originalUpdatedAt = workstream.UpdatedAt;
        var projectUpdatedAt = project.UpdatedAt;
        workstream.Name = validName;
        workstream.UpdatedAt = DateTimeOffset.UtcNow;
        project.UpdatedAt = workstream.UpdatedAt;

        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            workstream.Name = originalName;
            workstream.UpdatedAt = originalUpdatedAt;
            project.UpdatedAt = projectUpdatedAt;
            return new WorkstreamMutationResult(false, persistResult.Error);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new WorkstreamMutationResult(true, string.Empty, workstream);
    }

    public async Task<WorkstreamMutationResult> TryDeleteWorkstreamAsync(
        Guid projectId,
        Guid workstreamId,
        CancellationToken cancellationToken = default)
    {
        var project = Find(projectId);
        if (project is null)
        {
            return new WorkstreamMutationResult(false, "The selected project no longer exists.");
        }

        if (project.Workstreams.Count <= 1)
        {
            return new WorkstreamMutationResult(false, "At least one workstream must remain in a project.");
        }

        var index = project.Workstreams.FindIndex(item => item.Id == workstreamId);
        if (index < 0)
        {
            return new WorkstreamMutationResult(false, "The selected workstream no longer exists.");
        }

        var workstream = project.Workstreams[index];
        var originalUpdatedAt = project.UpdatedAt;
        project.Workstreams.RemoveAt(index);
        project.UpdatedAt = DateTimeOffset.UtcNow;

        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            project.Workstreams.Insert(index, workstream);
            project.UpdatedAt = originalUpdatedAt;
            return new WorkstreamMutationResult(false, persistResult.Error);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new WorkstreamMutationResult(true, string.Empty);
    }

    public async Task<WorkstreamMutationResult> TryChangeStateAsync(
        Guid projectId,
        Guid workstreamId,
        WorkflowState target,
        bool manualOverride,
        CancellationToken cancellationToken = default)
    {
        var project = Find(projectId);
        var workstream = project?.Workstreams.FirstOrDefault(item => item.Id == workstreamId);
        if (project is null || workstream is null)
        {
            return new WorkstreamMutationResult(false, "The selected workstream no longer exists.");
        }

        var originalState = workstream.CurrentState;
        var originalUpdatedAt = workstream.UpdatedAt;
        var projectUpdatedAt = project.UpdatedAt;
        if (!_stateMachine.TryTransition(workstream, target, manualOverride, out var error))
        {
            return new WorkstreamMutationResult(false, error);
        }

        project.UpdatedAt = workstream.UpdatedAt;
        var persistResult = await TryPersistAsync(cancellationToken);
        if (!persistResult.Success)
        {
            workstream.CurrentState = originalState;
            workstream.UpdatedAt = originalUpdatedAt;
            project.UpdatedAt = projectUpdatedAt;
            return new WorkstreamMutationResult(false, persistResult.Error);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new WorkstreamMutationResult(true, string.Empty, workstream);
    }

    public Task<ProjectMutationResult> TrySaveAsync(CancellationToken cancellationToken = default)
    {
        return TryPersistAsync(cancellationToken);
    }

    public Project? Find(Guid projectId)
    {
        return _state.Projects.FirstOrDefault(project => project.Id == projectId);
    }

    public Workstream? FindWorkstream(Guid projectId, Guid workstreamId)
    {
        return Find(projectId)?.Workstreams.FirstOrDefault(workstream => workstream.Id == workstreamId);
    }

    public Workstream? FindWorkstreamForId(Guid workstreamId)
    {
        return _state.Projects
            .SelectMany(project => project.Workstreams)
            .FirstOrDefault(workstream => workstream.Id == workstreamId);
    }

    private async Task<ProjectMutationResult> TryPersistAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            _saveGate.Release();
        }
    }
}
