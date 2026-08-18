using System.Text;

namespace BlueRelay.Services.Desktop;

public sealed record FocusedComposerElementMetadata(
    int ProcessId,
    IntPtr NativeWindowHandle,
    string ControlType,
    string LocalizedControlType,
    string AutomationId,
    string ClassName,
    string Name,
    string FrameworkId,
    bool IsEnabled,
    bool IsKeyboardFocusable,
    bool HasKeyboardFocus,
    bool IsOffscreen,
    UiAutomationBounds BoundingRectangle,
    IReadOnlyList<string> SupportedPatterns,
    bool? ValuePatternIsReadOnly = null);

public sealed record FocusedComposerWindowMetadata(
    int ProcessId,
    IntPtr Handle,
    string WindowTitle,
    string ClassName,
    string ProcessName,
    bool IsCodexDesktop);

public sealed record FocusedComposerProbeResult(
    bool Success,
    string Code,
    string Message,
    FocusedComposerElementMetadata? FocusedElement,
    IReadOnlyList<FocusedComposerElementMetadata> Parents,
    FocusedComposerWindowMetadata? Window,
    TimeSpan Duration)
{
    public static FocusedComposerProbeResult Failed(
        string code,
        string message,
        TimeSpan duration,
        FocusedComposerElementMetadata? focusedElement = null,
        IReadOnlyList<FocusedComposerElementMetadata>? parents = null,
        FocusedComposerWindowMetadata? window = null) =>
        new(false, code, message, focusedElement, parents ?? [], window, duration);

    public string ToDisplayText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Focused Composer Probe");
        builder.AppendLine("----------------------");
        builder.AppendLine($"Result={Code}");
        builder.AppendLine($"DurationMs={(long)Duration.TotalMilliseconds}");
        builder.AppendLine();
        builder.AppendLine("Window:");
        if (Window is null)
        {
            builder.AppendLine("(not available)");
        }
        else
        {
            builder.AppendLine($"PID={Window.ProcessId}");
            builder.AppendLine($"HWND=0x{Window.Handle.ToInt64():X}");
            builder.AppendLine($"Title={Window.WindowTitle}");
            builder.AppendLine($"Class={Window.ClassName}");
            builder.AppendLine($"Process={Window.ProcessName}");
            builder.AppendLine($"IsCodexDesktop={Window.IsCodexDesktop}");
        }

        builder.AppendLine();
        builder.AppendLine("Focused:");
        AppendElement(builder, FocusedElement);
        for (var index = 0; index < Parents.Count; index++)
        {
            builder.AppendLine();
            builder.AppendLine($"Parent[{index + 1}]:");
            AppendElement(builder, Parents[index]);
        }

        builder.AppendLine();
        builder.AppendLine($"Message={Message}");
        return builder.ToString().TrimEnd();
    }

    private static void AppendElement(StringBuilder builder, FocusedComposerElementMetadata? element)
    {
        if (element is null)
        {
            builder.AppendLine("(not available)");
            return;
        }

        builder.AppendLine($"PID={element.ProcessId}");
        builder.AppendLine($"HWND=0x{element.NativeWindowHandle.ToInt64():X}");
        builder.AppendLine($"ControlType={element.ControlType}");
        builder.AppendLine($"LocalizedControlType={element.LocalizedControlType}");
        builder.AppendLine($"AutomationId={element.AutomationId}");
        builder.AppendLine($"ClassName={element.ClassName}");
        builder.AppendLine($"Name={element.Name}");
        builder.AppendLine($"FrameworkId={element.FrameworkId}");
        builder.AppendLine($"Enabled={element.IsEnabled}");
        builder.AppendLine($"Focusable={element.IsKeyboardFocusable}");
        builder.AppendLine($"Focused={element.HasKeyboardFocus}");
        builder.AppendLine($"Offscreen={element.IsOffscreen}");
        builder.AppendLine($"Bounds={FormatBounds(element.BoundingRectangle)}");
        builder.AppendLine($"Patterns=[{string.Join(", ", element.SupportedPatterns)}]");
        builder.AppendLine($"ValuePatternIsReadOnly={element.ValuePatternIsReadOnly?.ToString() ?? "(unavailable)"}");
    }

    private static string FormatBounds(UiAutomationBounds bounds) =>
        $"{bounds.Left:0.##},{bounds.Top:0.##} {bounds.Width:0.##}x{bounds.Height:0.##}";
}
