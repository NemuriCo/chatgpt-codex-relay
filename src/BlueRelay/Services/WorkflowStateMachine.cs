using BlueRelay.Models;

namespace BlueRelay.Services;

public sealed class WorkflowStateMachine
{
    private static readonly IReadOnlyDictionary<WorkflowState, IReadOnlySet<WorkflowState>> AllowedTransitions =
        new Dictionary<WorkflowState, IReadOnlySet<WorkflowState>>
        {
            [WorkflowState.Idle] = new HashSet<WorkflowState>
            {
                WorkflowState.ReadyForCodex,
                WorkflowState.Error
            },
            [WorkflowState.ReadyForCodex] = new HashSet<WorkflowState>
            {
                WorkflowState.CodexRunning,
                WorkflowState.Error,
                WorkflowState.Idle
            },
            [WorkflowState.CodexRunning] = new HashSet<WorkflowState>
            {
                WorkflowState.ReadyForChatGPT,
                WorkflowState.Error
            },
            [WorkflowState.ReadyForChatGPT] = new HashSet<WorkflowState>
            {
                WorkflowState.ChatGPTReviewing,
                WorkflowState.Error,
                WorkflowState.Idle
            },
            [WorkflowState.ChatGPTReviewing] = new HashSet<WorkflowState>
            {
                WorkflowState.ReadyForCodex,
                WorkflowState.Completed,
                WorkflowState.Error
            },
            [WorkflowState.Completed] = new HashSet<WorkflowState>
            {
                WorkflowState.Idle,
                WorkflowState.ReadyForCodex
            },
            [WorkflowState.Error] = new HashSet<WorkflowState>
            {
                WorkflowState.Idle,
                WorkflowState.ReadyForCodex,
                WorkflowState.CodexRunning,
                WorkflowState.ReadyForChatGPT,
                WorkflowState.ChatGPTReviewing,
                WorkflowState.Completed
            }
        };

    public bool CanTransition(WorkflowState from, WorkflowState to)
    {
        return from == to || AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    public bool TryTransition(Workstream workstream, WorkflowState target, bool manualOverride, out string error)
    {
        if (!manualOverride && !CanTransition(workstream.CurrentState, target))
        {
            var from = WorkflowStateCatalog.Describe(workstream.CurrentState).Label;
            var to = WorkflowStateCatalog.Describe(target).Label;
            error = $"The workflow does not allow a transition from '{from}' to '{to}'.";
            return false;
        }

        workstream.CurrentState = target;
        workstream.UpdatedAt = DateTimeOffset.UtcNow;
        error = string.Empty;
        return true;
    }
}
