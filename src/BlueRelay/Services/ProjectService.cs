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

    public bool TryCreate(string name, string localPath, out Project? project, out string error)
    {
        project = null;
        if (!ProjectValidator.TryValidate(name, localPath, _state.Projects, null, out var validName, out var validPath, out error))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        project = new Project
        {
            Id = Guid.NewGuid(),
            Name = validName,
            LocalPath = validPath,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentState = WorkflowState.Idle
        };

        _state.Projects.Add(project);
        if (!TryPersist(out error))
        {
            _state.Projects.Remove(project);
            project = null;
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = string.Empty;
        return true;
    }

    public bool TryUpdate(Guid projectId, string name, string localPath, out string error)
    {
        var project = Find(projectId);
        if (project is null)
        {
            error = "The selected project no longer exists.";
            return false;
        }

        if (!ProjectValidator.TryValidate(name, localPath, _state.Projects, projectId, out var validName, out var validPath, out error))
        {
            return false;
        }

        var originalName = project.Name;
        var originalPath = project.LocalPath;
        var originalUpdatedAt = project.UpdatedAt;
        project.Name = validName;
        project.LocalPath = validPath;
        project.UpdatedAt = DateTimeOffset.UtcNow;

        if (!TryPersist(out error))
        {
            project.Name = originalName;
            project.LocalPath = originalPath;
            project.UpdatedAt = originalUpdatedAt;
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = string.Empty;
        return true;
    }

    public bool TryDelete(Guid projectId, out string error)
    {
        var index = _state.Projects.FindIndex(project => project.Id == projectId);
        if (index < 0)
        {
            error = "The selected project no longer exists.";
            return false;
        }

        var project = _state.Projects[index];
        _state.Projects.RemoveAt(index);
        var originalSelection = _state.SelectedProjectId;
        if (_state.SelectedProjectId == projectId)
        {
            _state.SelectedProjectId = _state.Projects.FirstOrDefault()?.Id;
        }

        if (!TryPersist(out error))
        {
            _state.Projects.Insert(index, project);
            _state.SelectedProjectId = originalSelection;
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = string.Empty;
        return true;
    }

    public bool TryChangeState(Guid projectId, WorkflowState target, bool manualOverride, out string error)
    {
        var project = Find(projectId);
        if (project is null)
        {
            error = "The selected project no longer exists.";
            return false;
        }

        var originalState = project.CurrentState;
        var originalUpdatedAt = project.UpdatedAt;
        if (!_stateMachine.TryTransition(project, target, manualOverride, out error))
        {
            return false;
        }

        if (!TryPersist(out error))
        {
            project.CurrentState = originalState;
            project.UpdatedAt = originalUpdatedAt;
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = string.Empty;
        return true;
    }

    public bool TrySave(out string error)
    {
        return TryPersist(out error);
    }

    public Project? Find(Guid projectId)
    {
        return _state.Projects.FirstOrDefault(project => project.Id == projectId);
    }

    private bool TryPersist(out string error)
    {
        try
        {
            _stateStore.SaveAsync(_state).GetAwaiter().GetResult();
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"BlueRelay could not save its local state. Details: {exception.Message}";
            return false;
        }
    }
}
