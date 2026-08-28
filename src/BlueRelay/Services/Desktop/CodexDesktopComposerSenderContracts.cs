namespace BlueRelay.Services.Desktop;

/// <summary>
/// In-memory identity for the composer that BlueRelay filled. It contains no
/// UI Automation objects and never stores the composer or task body.
/// </summary>
public sealed record CodexFillReceipt(
    Guid ProjectId,
    Guid WorkstreamId,
    Guid TaskId,
    long FillGeneration,
    IntPtr WindowHandle,
    int ProcessId,
    CodexComposerInjectionMode VerificationMode,
    DateTimeOffset FilledAtUtc,
    string? PreparedPayloadHash = null,
    string ComposerAutomationId = "",
    string ComposerClassName = "",
    string ComposerParentHierarchy = "",
    UiAutomationBounds? ComposerBounds = null);

public sealed record CodexSendButtonMetadata(
    string ControlType,
    string LocalizedControlType,
    string AutomationId,
    string ClassName,
    string FrameworkId,
    bool IsEnabled,
    bool IsOffscreen,
    bool InvokePatternAvailable,
    bool LegacyIAccessiblePatternAvailable,
    UiAutomationBounds Bounds,
    string Name = "");

public sealed record CodexComposerContentProbe(
    bool IsAvailable,
    CodexComposerContentState State,
    bool HasAttachmentOrReference)
{
    public bool HasSendableContent =>
        HasAttachmentOrReference || State == CodexComposerContentState.HasContent;
}

public sealed record CodexSendPostCheck(
    bool ComposerEmpty,
    bool SendButtonPresent,
    bool SendButtonEnabled);

public sealed record CodexComposerSendResult(
    bool Success,
    string Code,
    string Message,
    bool InvokeAttempted = false,
    bool InvokeSucceeded = false,
    bool ComposerEmpty = false,
    bool HasAttachmentOrReference = false,
    int CandidateCount = 0,
    bool Matched = false,
    string LocatorMethod = "",
    IReadOnlyList<CodexSendButtonMetadata>? ButtonCandidates = null,
    CodexSendPostCheck? PostCheck = null)
{
    public IReadOnlyList<CodexSendButtonMetadata> ButtonCandidatesSafe =>
        ButtonCandidates ?? Array.Empty<CodexSendButtonMetadata>();

    public static CodexComposerSendResult Failed(
        string code,
        string message,
        bool composerEmpty = false,
        bool hasAttachmentOrReference = false,
        int candidateCount = 0,
        string locatorMethod = "",
        IReadOnlyList<CodexSendButtonMetadata>? buttonCandidates = null) =>
        new(
            false,
            code,
            message,
            ComposerEmpty: composerEmpty,
            HasAttachmentOrReference: hasAttachmentOrReference,
            CandidateCount: candidateCount,
            LocatorMethod: locatorMethod,
            ButtonCandidates: buttonCandidates);
}

public interface ICodexDesktopComposerSender
{
    Task<CodexComposerSendResult> SendAsync(
        CodexFillReceipt receipt,
        CancellationToken cancellationToken = default);
}

public static class CodexSendButtonSelector
{
    private static readonly string[] SendTokens =
    [
        "send",
        "submit",
        "发送",
        "发消息"
    ];

    private static readonly string[] StableMetadataTokens =
    [
        "send",
        "submit",
        "message",
        "composer",
        "prompt"
    ];

    public static bool TrySelect(
        IReadOnlyList<CodexSendButtonMetadata> candidates,
        out CodexSendButtonMetadata? selected)
    {
        ArgumentNullException.ThrowIfNull(candidates);
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

        var best = eligible[0];
        if (best.Score < 50 ||
            (eligible.Count > 1 && best.Score == eligible[1].Score))
        {
            return false;
        }

        selected = best.Candidate;
        return true;
    }

    public static int Score(CodexSendButtonMetadata candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!IsEligible(candidate))
        {
            return int.MinValue;
        }

        var metadata = string.Join(
            " ",
            candidate.Name,
            candidate.LocalizedControlType,
            candidate.AutomationId,
            candidate.ClassName);
        var score = 30;
        if (ContainsAny(candidate.Name, SendTokens))
        {
            score += 45;
        }

        if (ContainsAny(candidate.AutomationId, SendTokens) ||
            ContainsAny(candidate.ClassName, SendTokens))
        {
            score += 30;
        }

        if (ContainsAny(metadata, StableMetadataTokens))
        {
            score += 10;
        }

        if (IsChromium(candidate.FrameworkId))
        {
            score += 10;
        }

        return score;
    }

    private static bool IsEligible(CodexSendButtonMetadata candidate)
    {
        if (!string.Equals(candidate.ControlType, "Button", StringComparison.OrdinalIgnoreCase) ||
            !candidate.InvokePatternAvailable ||
            !candidate.IsEnabled ||
            candidate.IsOffscreen ||
            candidate.Bounds.IsEmpty)
        {
            return false;
        }

        var metadata = string.Join(
            " ",
            candidate.Name,
            candidate.LocalizedControlType,
            candidate.AutomationId,
            candidate.ClassName);
        return IsChromium(candidate.FrameworkId) &&
               ContainsAny(metadata, SendTokens) &&
               ContainsAny(metadata, StableMetadataTokens);
    }

    private static bool IsChromium(string? frameworkId) =>
        string.Equals(frameworkId, "Chrome", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frameworkId, "Chromium", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frameworkId, "WebView2", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string? value, IEnumerable<string> tokens) =>
        tokens.Any(token => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);
}
