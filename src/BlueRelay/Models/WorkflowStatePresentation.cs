using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using BlueRelay.Localization;

namespace BlueRelay.Models;

public sealed record WorkflowStatePresentation(
    string Label,
    string Marker,
    MediaBrush Brush,
    string Guidance);

public static class WorkflowStateCatalog
{
    public static IReadOnlyList<WorkflowState> AllStates { get; } = Enum.GetValues<WorkflowState>();

    public static WorkflowStatePresentation Describe(WorkflowState state, UiTextSet? text = null)
    {
        text ??= LocalizationService.Current;
        return state switch
        {
            WorkflowState.Idle => Create(text.GetStateLabel(state), "○", "#8D96A5", text.GetStateGuidance(state)),
            WorkflowState.ReadyForCodex => Create(text.GetStateLabel(state), "→", "#62A8FF", text.GetStateGuidance(state)),
            WorkflowState.CodexRunning => Create(text.GetStateLabel(state), "◌", "#C49BFF", text.GetStateGuidance(state)),
            WorkflowState.ReadyForChatGPT => Create(text.GetStateLabel(state), "←", "#52D5B2", text.GetStateGuidance(state)),
            WorkflowState.ChatGPTReviewing => Create(text.GetStateLabel(state), "◒", "#F1C75B", text.GetStateGuidance(state)),
            WorkflowState.Completed => Create(text.GetStateLabel(state), "✓", "#62D37F", text.GetStateGuidance(state)),
            WorkflowState.NeedsAttention => Create(text.GetStateLabel(state), "!", "#FFB454", text.GetStateGuidance(state)),
            WorkflowState.Error => Create(text.GetStateLabel(state), "!", "#FF7272", text.GetStateGuidance(state)),
            _ => Create("?", "?", "#8D96A5", text.GetStateGuidance(WorkflowState.Error))
        };
    }

    private static WorkflowStatePresentation Create(string label, string marker, string color, string guidance)
    {
        var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));
        brush.Freeze();
        return new WorkflowStatePresentation(label, marker, brush, guidance);
    }
}
