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
    string Name = "",
    string HelpText = "");

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

/// <summary>
/// Whether the irreversible InvokePattern call has returned successfully.
/// </summary>
public enum CodexSendCommitState
{
    NotAttempted,
    NotCommitted,
    Committed
}

/// <summary>
/// Best-effort status for the non-blocking probe that follows a committed send.
/// </summary>
public enum CodexSendPostCheckStatus
{
    NotRun,
    Pending,
    Success,
    Timeout,
    Unavailable
}

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
    CodexSendPostCheck? PostCheck = null,
    CodexRunBaseline? RunBaseline = null,
    CodexSendCommitState CommitState = CodexSendCommitState.NotAttempted,
    CodexSendPostCheckStatus PostCheckStatus = CodexSendPostCheckStatus.NotRun)
{
    public IReadOnlyList<CodexSendButtonMetadata> ButtonCandidatesSafe =>
        ButtonCandidates ?? Array.Empty<CodexSendButtonMetadata>();

    public bool SendCommitted => CommitState == CodexSendCommitState.Committed;

    public bool IsCommitted => SendCommitted;

    public static CodexComposerSendResult Failed(
        string code,
        string message,
        bool composerEmpty = false,
        bool hasAttachmentOrReference = false,
        int candidateCount = 0,
        string locatorMethod = "",
        IReadOnlyList<CodexSendButtonMetadata>? buttonCandidates = null,
        bool invokeAttempted = false,
        bool invokeSucceeded = false) =>
        new(
            false,
            code,
            message,
            InvokeAttempted: invokeAttempted,
            InvokeSucceeded: invokeSucceeded,
            ComposerEmpty: composerEmpty,
            HasAttachmentOrReference: hasAttachmentOrReference,
            CandidateCount: candidateCount,
            LocatorMethod: locatorMethod,
            ButtonCandidates: buttonCandidates,
            CommitState: CodexSendCommitState.NotCommitted);
}

public static class CodexSendCommitPolicy
{
    public static bool IsRetryableFailure(CodexComposerSendResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !result.Success && !result.SendCommitted;
    }

    public static bool ShouldClearFillReceipt(CodexComposerSendResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.SendCommitted;
    }

    public static bool CanAdvanceToRun(CodexComposerSendResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.SendCommitted;
    }
}

public interface ICodexDesktopComposerSender
{
    Task<CodexComposerSendResult> SendAsync(
        CodexFillReceipt receipt,
        CancellationToken cancellationToken = default);
}

public interface ICodexDesktopComposerPostCheckScheduler
{
    void StartPostCheck(CodexComposerSendResult result);
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
