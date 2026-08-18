using System.Diagnostics;
using BlueRelay.Diagnostics;

namespace BlueRelay.Services.Desktop;

public sealed record UiAutomationBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record UiAutomationMetadata(
    IntPtr Handle,
    int ProcessId,
    string WindowTitle,
    string ControlType,
    string AutomationId,
    string Name,
    string ClassName,
    string FrameworkId,
    bool IsEnabled,
    bool IsKeyboardFocusable,
    bool IsOffscreen,
    UiAutomationBounds Bounds,
    UiAutomationBounds WindowBounds,
    string ParentHierarchy,
    bool IsLikelyOpenAiWindow,
    bool IsForeground);

public sealed record CodexComposerCandidate(
    UiAutomationMetadata Metadata,
    bool IsOpenAiWindow,
    bool SupportsValuePattern,
    bool IsValueReadOnly,
    int SemanticScore,
    bool SupportsTextPattern = false);

public sealed record OpenAiDesktopInspection(
    IReadOnlyList<UiAutomationMetadata> Windows,
    IReadOnlyList<CodexComposerCandidate> ComposerCandidates);

public sealed record CodexComposerInjectionResult(
    bool Success,
    string Code,
    string Message,
    bool UsedClipboardFallback = false,
    bool ClipboardRestoreFailed = false)
{
    public static CodexComposerInjectionResult Filled(string message, bool usedClipboardFallback = false, bool clipboardRestoreFailed = false) =>
        new(true, "filled", message, usedClipboardFallback, clipboardRestoreFailed);

    public static CodexComposerInjectionResult Failed(
        string code,
        string message,
        bool clipboardRestoreFailed = false) =>
        new(false, code, message, ClipboardRestoreFailed: clipboardRestoreFailed);
}

public interface ICodexDesktopComposerInjector
{
    Task<OpenAiDesktopInspection> InspectOpenAiDesktopWindowsAsync(CancellationToken cancellationToken = default);

    Task<CodexComposerInjectionResult> InjectAsync(string text, CancellationToken cancellationToken = default);
}

public static class CodexComposerDiagnostics
{
    public static void WriteStage(string stage, Stopwatch? stopwatch = null)
    {
        var elapsed = stopwatch?.ElapsedMilliseconds ?? 0;
        StartupDiagnostics.Write($"Codex composer stage={stage} elapsedMs={elapsed}");
    }
}

public static class CodexComposerCandidateSelector
{
    private static readonly string[] ComposerClassTokens =
    [
        "ProseMirror",
        "RichTextInput",
        "ComposerLayout",
        "composer"
    ];

    private static readonly string[] ParentComposerTokens =
    [
        "ProseMirror",
        "RichTextInput",
        "ComposerLayout",
        "composer",
        "prompt",
        "message",
        "input",
        "editor"
    ];

    private static readonly string[] SemanticTokens =
    [
        "composer",
        "prompt",
        "message",
        "input",
        "editor",
        "chat"
    ];

    private static readonly string[] NonComposerTokens =
    [
        "send",
        "submit",
        "search",
        "settings",
        "navigation"
    ];

    public static bool TrySelect(
        IReadOnlyList<CodexComposerCandidate> candidates,
        out CodexComposerCandidate? selected)
    {
        selected = null;
        var eligible = candidates
            .Where(IsEligible)
            .Select(candidate => (Candidate: candidate, Score: Score(candidate)))
            .OrderByDescending(item => item.Score)
            .ToList();

        if (eligible.Count == 0)
        {
            return false;
        }

        // Never guess across multiple OpenAI windows. The user may have more
        // than one project or session open, and this MVP has no safe way to
        // infer which one they intended after clicking BlueRelay.
        if (eligible.Select(item => item.Candidate.Metadata.Handle).Distinct().Count() != 1)
        {
            return false;
        }

        var best = eligible[0];
        var second = eligible.Count > 1 ? eligible[1] : default;
        if (eligible.Count > 1 && best.Score - second.Score < 15)
        {
            return false;
        }

        if (best.Score < 60)
        {
            return false;
        }

        selected = best.Candidate;
        return true;
    }

    public static int Score(CodexComposerCandidate candidate)
    {
        if (!IsEligible(candidate))
        {
            return int.MinValue;
        }

        var metadata = candidate.Metadata;
        var score = metadata.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            ? 58
            : metadata.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase)
                ? 54
                : 42;

        score += candidate.IsOpenAiWindow ? 18 : 0;
        score += metadata.IsKeyboardFocusable ? 10 : 0;
        score += candidate.SupportsValuePattern && !candidate.IsValueReadOnly ? 10 : 0;
        score += candidate.SupportsTextPattern ? 8 : 0;
        score += IsChromiumFramework(metadata.FrameworkId) ? 12 : 0;
        score += ContainsToken(metadata.ClassName, ComposerClassTokens) ? 55 : 0;
        score += ContainsToken(metadata.ParentHierarchy, ParentComposerTokens) ? 20 : 0;
        score += SemanticSignal(metadata.AutomationId, metadata.Name, metadata.ClassName);
        score += RelativeBottomSignal(metadata);
        return score;
    }

    private static bool IsEligible(CodexComposerCandidate candidate)
    {
        var metadata = candidate.Metadata;
        var isEditableControl = metadata.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                                metadata.ControlType.Equals("Document", StringComparison.OrdinalIgnoreCase) ||
                                metadata.ControlType.Equals("Custom", StringComparison.OrdinalIgnoreCase) ||
                                metadata.ControlType.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
                                metadata.ControlType.Equals("Pane", StringComparison.OrdinalIgnoreCase);
        var hasEditablePattern = candidate.SupportsValuePattern || candidate.SupportsTextPattern;
        var hasComposerSignal = ContainsToken(
            metadata.ClassName,
            metadata.AutomationId,
            metadata.Name,
            metadata.ParentHierarchy,
            ParentComposerTokens);
        var isChromiumCandidate = IsChromiumFramework(metadata.FrameworkId);
        return candidate.IsOpenAiWindow &&
               metadata.Handle != IntPtr.Zero &&
               isEditableControl &&
               metadata.IsEnabled &&
               metadata.IsKeyboardFocusable &&
               !metadata.IsOffscreen &&
               !metadata.Bounds.IsEmpty &&
               hasEditablePattern &&
               (!isChromiumCandidate || hasComposerSignal) &&
               !ContainsToken(string.Empty, metadata.AutomationId, metadata.Name, metadata.ParentHierarchy, NonComposerTokens);
    }

    private static int SemanticSignal(string automationId, string name, string className)
    {
        var signal = 0;
        if (ContainsToken(className, automationId, name, string.Empty, SemanticTokens))
        {
            signal += 22;
        }

        return signal;
    }

    private static int RelativeBottomSignal(UiAutomationMetadata metadata)
    {
        if (metadata.Bounds.IsEmpty)
        {
            return 0;
        }

        // A composer is commonly near the bottom of its window. This is only a
        // relative accessibility-tree signal; it is never a screen coordinate.
        var windowHeight = metadata.WindowBounds.Height;
        var relativePosition = windowHeight <= 0
            ? 0
            : (metadata.Bounds.Bottom - metadata.WindowBounds.Top) / windowHeight;
        return relativePosition >= 0.75 ? 5 : 0;
    }

    private static bool IsChromiumFramework(string frameworkId) =>
        frameworkId.Equals("Chrome", StringComparison.OrdinalIgnoreCase) ||
        frameworkId.Equals("Chromium", StringComparison.OrdinalIgnoreCase) ||
        frameworkId.Equals("WebView2", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsToken(string value, IEnumerable<string> tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsToken(
        string className,
        string automationId,
        string name,
        string parentHierarchy,
        IEnumerable<string> tokens)
    {
        return ContainsToken($"{className} {automationId} {name} {parentHierarchy}", tokens);
    }
}
