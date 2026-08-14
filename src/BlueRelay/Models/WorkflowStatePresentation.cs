using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace BlueRelay.Models;

public sealed record WorkflowStatePresentation(
    string Label,
    string Marker,
    MediaBrush Brush,
    string Guidance);

public static class WorkflowStateCatalog
{
    public static IReadOnlyList<WorkflowState> AllStates { get; } = Enum.GetValues<WorkflowState>();

    public static WorkflowStatePresentation Describe(WorkflowState state)
    {
        return state switch
        {
            WorkflowState.Idle => Create("Waiting for a new task", "○", "#8D96A5", "Start a new task when ready."),
            WorkflowState.ReadyForCodex => Create("Next: send to Codex", "→", "#62A8FF", "The task is ready for Codex."),
            WorkflowState.CodexRunning => Create("Codex is running", "◌", "#C49BFF", "No action is needed right now."),
            WorkflowState.ReadyForChatGPT => Create("Next: send to ChatGPT", "←", "#52D5B2", "Hand the Codex result back for review."),
            WorkflowState.ChatGPTReviewing => Create("Waiting for ChatGPT review", "◒", "#F1C75B", "Wait for the next review decision."),
            WorkflowState.Completed => Create("Round completed", "✓", "#62D37F", "This task round is closed."),
            WorkflowState.Error => Create("Needs attention", "!", "#FF7272", "Resolve the issue before continuing."),
            _ => Create("Unknown state", "?", "#8D96A5", "Select a valid workflow state.")
        };
    }

    private static WorkflowStatePresentation Create(string label, string marker, string color, string guidance)
    {
        var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));
        brush.Freeze();
        return new WorkflowStatePresentation(label, marker, brush, guidance);
    }
}
