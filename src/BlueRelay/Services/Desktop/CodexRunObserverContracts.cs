using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlueRelay.Services.Desktop;

public enum CodexRunControlState
{
    Unknown,
    Running,
    NotRunning,
    // Kept as a source-compatible alias for callers written against the
    // first observer prototype. New code should use NotRunning.
    Idle = NotRunning
}

public enum CodexRunCompletionState
{
    WaitingForRunStart,
    RunningObserved,
    CompletionCandidate,
    Completed
}

public sealed record CodexRunObserverOptions(
    TimeSpan PollInterval,
    TimeSpan QuietWindow,
    TimeSpan FallbackQuietWindow,
    TimeSpan Timeout)
{
    public static CodexRunObserverOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(850),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(9),
        TimeSpan.FromMinutes(60));

    public CodexRunObserverOptions Validate()
    {
        if (PollInterval <= TimeSpan.Zero ||
            QuietWindow <= TimeSpan.Zero ||
            FallbackQuietWindow <= TimeSpan.Zero ||
            Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "Observer timing values must be positive.");
        }

        return this;
    }
}

public sealed record CodexRunCompletionDecision(
    CodexRunCompletionState State,
    bool RunningObserved,
    bool CompletionCandidate,
    bool IsComplete,
    TimeSpan QuietFor,
    bool RunningReturned);

/// <summary>
/// Pure completion state machine. UI Automation only supplies the current
/// control state and output-change counters; this class owns the transition
/// from a disappearing run control to a quiet completion candidate.
/// </summary>
public sealed class CodexRunCompletionTracker
{
    private readonly TimeSpan _quietWindow;
    private DateTimeOffset? _activityAtUtc;
    private DateTimeOffset? _candidateSinceUtc;

    public CodexRunCompletionTracker(TimeSpan quietWindow)
    {
        if (quietWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietWindow));
        }

        _quietWindow = quietWindow;
    }

    public CodexRunCompletionState State { get; private set; } = CodexRunCompletionState.WaitingForRunStart;

    public bool RunningObserved { get; private set; }

    public bool CompletionCandidate => State == CodexRunCompletionState.CompletionCandidate;

    public CodexRunCompletionDecision Observe(
        CodexRunControlState controlState,
        int outputCount,
        int newOutputCount,
        int changedOutputCount,
        DateTimeOffset observedAtUtc)
    {
        if (State == CodexRunCompletionState.Completed)
        {
            return new(
                State,
                RunningObserved,
                CompletionCandidate: true,
                IsComplete: true,
                QuietFor: _quietWindow,
                RunningReturned: false);
        }

        if (newOutputCount > 0 || changedOutputCount > 0)
        {
            _activityAtUtc = observedAtUtc;
        }

        var runningReturned = controlState == CodexRunControlState.Running &&
                              State == CodexRunCompletionState.CompletionCandidate;
        if (controlState == CodexRunControlState.Running)
        {
            RunningObserved = true;
            State = CodexRunCompletionState.RunningObserved;
            _candidateSinceUtc = null;
            // A returned running control is activity in its own right. This
            // prevents a transient control disappearance from completing as
            // soon as the next quiet poll arrives.
            _activityAtUtc = observedAtUtc;
        }
        else if (controlState == CodexRunControlState.NotRunning && RunningObserved)
        {
            if (State != CodexRunCompletionState.CompletionCandidate)
            {
                State = CodexRunCompletionState.CompletionCandidate;
                _candidateSinceUtc = observedAtUtc;
                _activityAtUtc ??= observedAtUtc;
            }

            var quietAnchor = Max(_activityAtUtc, _candidateSinceUtc) ?? observedAtUtc;
            var quietFor = observedAtUtc - quietAnchor;
            var complete = outputCount > 0 && quietFor >= _quietWindow;
            if (complete)
            {
                State = CodexRunCompletionState.Completed;
            }

            return new(
                State,
                RunningObserved,
                State is CodexRunCompletionState.CompletionCandidate or CodexRunCompletionState.Completed,
                complete,
                quietFor,
                RunningReturned: false);
        }
        else if (controlState == CodexRunControlState.Unknown &&
                 State == CodexRunCompletionState.CompletionCandidate)
        {
            // Unknown is not proof that the run ended. Wait for a trusted
            // NotRunning observation instead of manufacturing completion.
            State = CodexRunCompletionState.RunningObserved;
            _candidateSinceUtc = null;
        }

        var currentQuietFor = State == CodexRunCompletionState.CompletionCandidate &&
                              _candidateSinceUtc is { } candidateSince
            ? observedAtUtc - (Max(_activityAtUtc, candidateSince) ?? candidateSince)
            : TimeSpan.Zero;
        return new(
            State,
            RunningObserved,
            false,
            false,
            currentQuietFor,
            runningReturned);
    }

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first.Value >= second.Value ? first : second;
    }
}

public sealed record CodexAssistantActionStructure(
    bool InThreadScope,
    bool InComposer,
    bool HasCopyButton,
    int ActionButtonCount,
    bool HasFeedbackAction,
    bool HasBodyContent)
{
    public bool IsTrustedAssistantBlock =>
        InThreadScope &&
        !InComposer &&
        HasCopyButton &&
        HasBodyContent &&
        (HasFeedbackAction ? ActionButtonCount >= 2 : ActionButtonCount >= 3);
}

public static class CodexAssistantBlockClassifier
{
    public static bool IsTrustedAssistantBlock(CodexAssistantActionStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return structure.IsTrustedAssistantBlock;
    }
}

public enum CodexRunCompletionMode
{
    NativeRunControl,
    FallbackQuietWindow,
    UnknownIdle,
    NoOutputs,
    CaptureIncomplete,
    BoundarySuperseded,
    Timeout,
    Cancelled,
    ObserverUnavailable
}

public enum CodexRunOutputKind
{
    AssistantText
}

public enum CodexRunCaptureMethod
{
    NativeCopy,
    UiaPlainText
}

/// <summary>
/// Metadata for one assistant block observed before BlueRelay invokes Send.
/// It intentionally contains no response body.
/// </summary>
public sealed record CodexRunBlockBaseline(
    int SequenceIndex,
    string StructuralFingerprint,
    string ParentStructuralFingerprint,
    int TextLength,
    string TextPrefixHash);

public sealed record CodexRunBaseline(
    bool IsAvailable,
    int AssistantBlockCount,
    IReadOnlyList<CodexRunBlockBaseline> OrderedBlocks,
    IReadOnlyList<CodexRunUserMessageBaseline>? OrderedUserMessages = null)
{
    public static CodexRunBaseline Unavailable { get; } = new(false, 0, []);

    public IReadOnlyList<CodexRunBlockBaseline> OrderedBlocksSafe =>
        OrderedBlocks ?? Array.Empty<CodexRunBlockBaseline>();

    public IReadOnlyList<CodexRunUserMessageBaseline> OrderedUserMessagesSafe =>
        OrderedUserMessages ?? Array.Empty<CodexRunUserMessageBaseline>();
}

/// <summary>
/// Baseline metadata for a user-message action. It is deliberately body-free;
/// the prefix hash is only used to distinguish a historical message from a
/// newly rendered message with the same structural ancestry.
/// </summary>
public sealed record CodexRunUserMessageBaseline(
    int Ordinal,
    string StructuralFingerprint,
    string ParentStructuralFingerprint,
    string ActionRowStructuralFingerprint,
    int TextLength,
    string TextPrefixHash,
    string ContentSignature);

public enum CodexRunThreadItemKind
{
    Unknown,
    UserMessage,
    AssistantOutput
}

public sealed record CodexRunSemanticMatch(
    int SemanticAnchorCount,
    int MatchedAnchorCount,
    bool Matched);

/// <summary>
/// Hashed semantic anchors for the task sent by BlueRelay. No task text is
/// retained in a run receipt or written to diagnostics.
/// </summary>
public sealed record CodexRunSemanticAnchors(IReadOnlyList<string> AnchorHashes)
{
    private const int MaximumAnchorCount = 16;

    public IReadOnlyList<string> AnchorHashesSafe =>
        AnchorHashes ?? Array.Empty<string>();

    public int Count => AnchorHashesSafe.Count;

    public static CodexRunSemanticAnchors FromText(string? text)
    {
        var normalized = NormalizeSemanticText(text);
        var tokens = Regex.Split(normalized, @"[^\p{L}\p{Nd}]+")
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumAnchorCount)
            .Select(ComputeHash)
            .ToList();
        return new CodexRunSemanticAnchors(tokens);
    }

    public CodexRunSemanticMatch Match(string? text)
    {
        var anchors = AnchorHashesSafe;
        if (anchors.Count == 0)
        {
            return new(0, 0, false);
        }

        var candidateHashes = Regex.Split(NormalizeSemanticText(text), @"[^\p{L}\p{Nd}]+")
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Select(ComputeHash)
            .ToHashSet(StringComparer.Ordinal);
        var matched = anchors.Count(anchor => candidateHashes.Contains(anchor));
        var required = anchors.Count <= 2
            ? anchors.Count
            : Math.Max(2, (anchors.Count + 1) / 2);
        return new(anchors.Count, matched, matched >= required);
    }

    private static string NormalizeSemanticText(string? text) =>
        Regex.Replace(
                (text ?? string.Empty)
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n'),
                @"[ \t]+",
                " ")
            .Trim()
            .ToLowerInvariant();

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Body-bearing observation used only in memory while a snapshot is being
/// reconciled. The public projection contains only bounded metadata and is
/// never logged with the body.
/// </summary>
public sealed record CodexRunThreadActionObservation(
    CodexRunThreadItemKind Kind,
    string StructuralFingerprint,
    string ParentStructuralFingerprint,
    string ActionRowStructuralFingerprint,
    string ActionDescriptor,
    string Text,
    UiAutomationBounds Bounds,
    int SourceOrdinal = 0,
    string ContentSignature = "",
    int LineCount = 0)
{
    public int TextLength => Text?.Length ?? 0;

    public string ContentSignatureSafe =>
        string.IsNullOrWhiteSpace(ContentSignature)
            ? CodexRunOutputAccumulator.ComputeContentSignature(Text)
            : ContentSignature;

    public int LineCountSafe =>
        LineCount > 0
            ? LineCount
            : CodexRunOutputAccumulator.CountLines(Text);
}

public sealed record CodexRunOrderedThreadItem(
    CodexRunThreadItemKind Kind,
    int Ordinal,
    int SourceOrdinal,
    string StructuralFingerprint,
    string ParentStructuralFingerprint,
    string ActionRowStructuralFingerprint,
    string ActionDescriptor,
    string Text,
    UiAutomationBounds Bounds,
    int TextLength,
    int LineCount,
    string ContentSignature);

public sealed record CodexRunThreadOrderProjection(
    IReadOnlyList<CodexRunOrderedThreadItem> Items,
    string Method)
{
    public IReadOnlyList<CodexRunOrderedThreadItem> ItemsSafe =>
        Items ?? Array.Empty<CodexRunOrderedThreadItem>();
}

/// <summary>
/// Produces an oldest-to-newest action projection. Codex currently renders
/// the thread with flex-col-reverse, so visual Y is preferred when available;
/// raw/UIA order is used only as a deterministic fallback.
/// </summary>
public static class OrderedThreadActionProjection
{
    public static CodexRunThreadOrderProjection NormalizeThreadOrder(
        IReadOnlyList<CodexRunThreadActionObservation> observations,
        bool isFlexColumnReverse = false)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var source = observations
            .Select((observation, index) => new
            {
                Observation = observation,
                SourceOrdinal = observation.SourceOrdinal == 0 && index > 0
                    ? index
                    : observation.SourceOrdinal
            })
            .ToList();
        var hasUsableBounds = source.Count > 0 && source.All(item => !item.Observation.Bounds.IsEmpty);
        var ordered = hasUsableBounds
            ? source
                .OrderBy(item => item.Observation.Bounds.Top)
                .ThenBy(item => item.Observation.Bounds.Left)
                .ThenBy(item => item.SourceOrdinal)
                .ToList()
            : source
                .OrderBy(item => item.SourceOrdinal)
                .ToList();

        var method = hasUsableBounds
            ? isFlexColumnReverse ? "bounds_y_ascending_flex_col_reverse" : "bounds_y_ascending"
            : isFlexColumnReverse ? "uia_raw_order_flex_col_reverse_fallback" : "uia_raw_order_fallback";
        return new(
            ordered
                .Select((item, ordinal) => new CodexRunOrderedThreadItem(
                    item.Observation.Kind,
                    ordinal,
                    item.SourceOrdinal,
                    item.Observation.StructuralFingerprint,
                    item.Observation.ParentStructuralFingerprint,
                    item.Observation.ActionRowStructuralFingerprint,
                    item.Observation.ActionDescriptor,
                    item.Observation.Text,
                    item.Observation.Bounds,
                    item.Observation.TextLength,
                    item.Observation.LineCountSafe,
                    item.Observation.ContentSignatureSafe))
                .ToList(),
            method);
    }
}

public sealed record CodexRunBoundary(
    Guid TaskId,
    long Generation,
    DateTimeOffset FirstObservedAtUtc,
    int Ordinal,
    string StructuralFingerprint,
    string ParentStructuralFingerprint,
    string ActionRowStructuralFingerprint,
    string ContentSignature,
    int SemanticAnchorCount,
    int MatchedAnchorCount,
    bool MatchedCurrentTask);

public sealed record CodexRunReconciliation(
    IReadOnlyList<CodexRunOutput> Outputs,
    CodexRunBoundary? Boundary,
    bool BoundarySuperseded,
    string ThreadOrderMethod,
    int BoundaryOrdinal,
    IReadOnlyList<int> AssistantOrdinalsAfterBoundary)
{
    public IReadOnlyList<CodexRunOutput> OutputsSafe =>
        Outputs ?? Array.Empty<CodexRunOutput>();

    public IReadOnlyList<int> AssistantOrdinalsAfterBoundarySafe =>
        AssistantOrdinalsAfterBoundary ?? Array.Empty<int>();

    public bool BoundaryConfirmed => Boundary is not null;
}

public sealed record CodexRunReceipt(
    Guid RunId,
    Guid ProjectId,
    Guid WorkstreamId,
    Guid TaskId,
    long Generation,
    IntPtr WindowHandle,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    CodexComposerInjectionMode FillVerificationMode,
    CodexRunBaseline PreSendBaseline,
    string ComposerAutomationId = "",
    string ComposerClassName = "",
    string ComposerParentHierarchy = "",
    UiAutomationBounds? ComposerBounds = null,
    CodexRunSemanticAnchors? CurrentTaskSemanticAnchors = null);

/// <summary>
/// A single assistant response in document order. The body is retained only in
/// the in-memory run result until the aggregate payload is written atomically.
/// </summary>
public sealed record CodexRunOutput(
    int SequenceIndex,
    string StructuralFingerprint,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    CodexRunOutputKind Kind,
    string Text,
    CodexRunCaptureMethod CaptureMethod);

public sealed record CodexRunResult(
    Guid RunId,
    Guid ProjectId,
    Guid WorkstreamId,
    Guid TaskId,
    long Generation,
    IntPtr WindowHandle,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    CodexRunCompletionMode CompletionMode,
    IReadOnlyList<CodexRunOutput> Outputs,
    bool Success,
    string Code = "",
    string Error = "",
    bool IsPartial = false)
{
    public IReadOnlyList<CodexRunOutput> OutputsSafe => Outputs ?? Array.Empty<CodexRunOutput>();

    public string CaptureMethodSummary => string.Join(
        ",",
        OutputsSafe
            .GroupBy(output => output.CaptureMethod)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}"));

    public static CodexRunResult Failure(
        CodexRunReceipt receipt,
        CodexRunCompletionMode completionMode,
        string code,
        string error,
        IReadOnlyList<CodexRunOutput>? outputs = null,
        bool isPartial = false,
        DateTimeOffset? completedAtUtc = null) =>
        new(
            receipt.RunId,
            receipt.ProjectId,
            receipt.WorkstreamId,
            receipt.TaskId,
            receipt.Generation,
            receipt.WindowHandle,
            receipt.ProcessId,
            receipt.StartedAtUtc,
            completedAtUtc,
            completionMode,
            outputs ?? Array.Empty<CodexRunOutput>(),
            false,
            code,
            error,
            isPartial);
}

/// <summary>
/// A bounded, UIA-independent snapshot used by the observer and by tests.
/// </summary>
public sealed record CodexRunBlockObservation(
    int SequenceIndex,
    string StructuralFingerprint,
    string ParentStructuralFingerprint,
    string Text,
    bool HasNativeCopy,
    string TextPrefixHash = "",
    UiAutomationBounds? Bounds = null,
    string ActionRowStructuralFingerprint = "",
    string ContentSignature = "",
    int LineCount = 0)
{
    public string ContentSignatureSafe =>
        string.IsNullOrWhiteSpace(ContentSignature)
            ? CodexRunOutputAccumulator.ComputeContentSignature(Text)
            : ContentSignature;

    public int LineCountSafe =>
        LineCount > 0 ? LineCount : CodexRunOutputAccumulator.CountLines(Text);
}

/// <summary>
/// Reconciles repeated UIA snapshots without using RuntimeId as identity.
/// Streaming growth updates one output; a DOM rerender can change the
/// structural fingerprint and still reconcile by sequence, parent, and prefix.
/// </summary>
public sealed class CodexRunOutputAccumulator
{
    private sealed class TrackedOutput
    {
        public required int SequenceIndex { get; set; }
        public required string StructuralFingerprint { get; set; }
        public required string ParentStructuralFingerprint { get; set; }
        public required DateTimeOffset FirstObservedAtUtc { get; init; }
        public required DateTimeOffset LastObservedAtUtc { get; set; }
        public required string Text { get; set; }
        public required string ContentSignature { get; set; }
    }

    private readonly CodexRunBaseline _baseline;
    private readonly List<TrackedOutput> _outputs = [];
    private readonly List<int> _lastNewOutputIndices = [];
    private readonly List<int> _lastChangedOutputIndices = [];
    private readonly List<int> _lastSignatureChangedOutputIndices = [];
    private CodexRunBoundary? _boundary;
    private bool _boundarySuperseded;

    public CodexRunOutputAccumulator(CodexRunBaseline baseline)
    {
        _baseline = baseline ?? CodexRunBaseline.Unavailable;
    }

    public int LastNewOutputCount { get; private set; }

    public int LastChangedOutputCount { get; private set; }

    public IReadOnlyList<int> LastNewOutputIndices => _lastNewOutputIndices;

    public IReadOnlyList<int> LastChangedOutputIndices => _lastChangedOutputIndices;

    public IReadOnlyList<int> LastSignatureChangedOutputIndices => _lastSignatureChangedOutputIndices;

    public bool BoundaryConfirmed => _boundary is not null;

    public bool BoundarySuperseded => _boundarySuperseded;

    public CodexRunBoundary? Boundary => _boundary;

    public CodexRunReconciliation ApplyThreadSnapshot(
        IReadOnlyList<CodexRunThreadActionObservation> snapshot,
        DateTimeOffset observedAtUtc,
        Guid taskId,
        long generation,
        CodexRunSemanticAnchors? currentTaskSemanticAnchors,
        bool isFlexColumnReverse = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projection = OrderedThreadActionProjection.NormalizeThreadOrder(
            snapshot,
            isFlexColumnReverse);
        return ApplyOrderedThreadSnapshot(
            projection.ItemsSafe,
            projection.Method,
            observedAtUtc,
            taskId,
            generation,
            currentTaskSemanticAnchors);
    }

    public CodexRunReconciliation ApplyOrderedThreadSnapshot(
        IReadOnlyList<CodexRunOrderedThreadItem> snapshot,
        string threadOrderMethod,
        DateTimeOffset observedAtUtc,
        Guid taskId,
        long generation,
        CodexRunSemanticAnchors? currentTaskSemanticAnchors)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        threadOrderMethod ??= string.Empty;
        LastNewOutputCount = 0;
        LastChangedOutputCount = 0;
        _lastNewOutputIndices.Clear();
        _lastChangedOutputIndices.Clear();
        _lastSignatureChangedOutputIndices.Clear();

        if (_boundary is null && !_boundarySuperseded)
        {
            ConfirmBoundaryIfPresent(
                snapshot,
                observedAtUtc,
                taskId,
                generation,
                currentTaskSemanticAnchors);
        }

        var boundaryOrdinal = _boundary?.Ordinal ?? -1;
        var boundaryIndex = FindBoundaryIndex(snapshot);
        var assistantAfterBoundary = new List<CodexRunOrderedThreadItem>();
        if (_boundary is not null && !_boundarySuperseded)
        {
            var startIndex = boundaryIndex >= 0
                ? boundaryIndex + 1
                : snapshot.TakeWhile(item => item.Ordinal <= boundaryOrdinal).Count();
            for (var index = startIndex; index < snapshot.Count; index++)
            {
                var item = snapshot[index];
                if (item.Kind == CodexRunThreadItemKind.UserMessage)
                {
                    _boundarySuperseded = true;
                    break;
                }

                if (item.Kind == CodexRunThreadItemKind.AssistantOutput &&
                    !string.IsNullOrWhiteSpace(item.Text))
                {
                    assistantAfterBoundary.Add(item);
                }
            }
        }

        if (!_boundarySuperseded)
        {
            for (var slot = 0; slot < assistantAfterBoundary.Count; slot++)
            {
                ApplyOutputSlot(assistantAfterBoundary[slot], slot, observedAtUtc);
            }
        }

        return new(
            SnapshotOutputs(),
            _boundary,
            _boundarySuperseded,
            threadOrderMethod,
            boundaryOrdinal,
            assistantAfterBoundary.Select(item => item.Ordinal).ToList());
    }

    public IReadOnlyList<CodexRunOutput> Apply(
        IReadOnlyList<CodexRunBlockObservation> snapshot,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LastNewOutputCount = 0;
        LastChangedOutputCount = 0;
        _lastNewOutputIndices.Clear();
        _lastChangedOutputIndices.Clear();
        _lastSignatureChangedOutputIndices.Clear();

        var visible = snapshot
            .Where(observation => !string.IsNullOrWhiteSpace(observation.Text))
            .Where(observation => !IsHistorical(observation))
            .OrderBy(observation => observation.SequenceIndex)
            .ToList();

        foreach (var observation in visible)
        {
            var tracked = FindTracked(observation);
            if (tracked is null)
            {
                tracked = new TrackedOutput
                {
                    SequenceIndex = observation.SequenceIndex,
                    StructuralFingerprint = observation.StructuralFingerprint,
                    ParentStructuralFingerprint = observation.ParentStructuralFingerprint,
                    FirstObservedAtUtc = observedAtUtc,
                    LastObservedAtUtc = observedAtUtc,
                    Text = observation.Text,
                    ContentSignature = GetContentSignature(observation)
                };
                _outputs.Add(tracked);
                LastNewOutputCount++;
                _lastNewOutputIndices.Add(observation.SequenceIndex);
                continue;
            }

            var changed = !string.Equals(tracked.Text, observation.Text, StringComparison.Ordinal) ||
                          tracked.SequenceIndex != observation.SequenceIndex ||
                          !string.Equals(
                              tracked.StructuralFingerprint,
                              observation.StructuralFingerprint,
                              StringComparison.Ordinal);
            tracked.SequenceIndex = observation.SequenceIndex;
            tracked.StructuralFingerprint = observation.StructuralFingerprint;
            tracked.ParentStructuralFingerprint = observation.ParentStructuralFingerprint;
            tracked.LastObservedAtUtc = observedAtUtc;
            tracked.Text = observation.Text;
            tracked.ContentSignature = GetContentSignature(observation);
            if (changed)
            {
                LastChangedOutputCount++;
                _lastChangedOutputIndices.Add(observation.SequenceIndex);
            }
        }

        return SnapshotOutputs();
    }

    public IReadOnlyList<CodexRunOutput> SnapshotOutputs() =>
        _outputs
            .OrderBy(output => output.SequenceIndex)
            .Select(output => new CodexRunOutput(
                output.SequenceIndex,
                output.StructuralFingerprint,
                output.FirstObservedAtUtc,
                output.LastObservedAtUtc,
                CodexRunOutputKind.AssistantText,
                output.Text,
                CodexRunCaptureMethod.UiaPlainText))
            .ToList();

    private void ConfirmBoundaryIfPresent(
        IReadOnlyList<CodexRunOrderedThreadItem> snapshot,
        DateTimeOffset observedAtUtc,
        Guid taskId,
        long generation,
        CodexRunSemanticAnchors? currentTaskSemanticAnchors)
    {
        if (currentTaskSemanticAnchors is null)
        {
            return;
        }

        foreach (var item in snapshot
                     .Where(item => item.Kind == CodexRunThreadItemKind.UserMessage)
                     .OrderBy(item => item.Ordinal))
        {
            if (IsHistoricalUserMessage(item))
            {
                continue;
            }

            var semanticMatch = currentTaskSemanticAnchors.Match(item.Text);
            if (!semanticMatch.Matched)
            {
                continue;
            }

            _boundary = new CodexRunBoundary(
                taskId,
                generation,
                observedAtUtc,
                item.Ordinal,
                item.StructuralFingerprint,
                item.ParentStructuralFingerprint,
                item.ActionRowStructuralFingerprint,
                item.ContentSignature,
                semanticMatch.SemanticAnchorCount,
                semanticMatch.MatchedAnchorCount,
                semanticMatch.Matched);
            return;
        }
    }

    private int FindBoundaryIndex(IReadOnlyList<CodexRunOrderedThreadItem> snapshot)
    {
        if (_boundary is null)
        {
            return -1;
        }

        var exact = snapshot
            .Select((item, index) => (item, index))
            .Where(pair =>
                pair.item.Kind == CodexRunThreadItemKind.UserMessage &&
                string.Equals(
                    pair.item.ContentSignature,
                    _boundary.ContentSignature,
                    StringComparison.Ordinal))
            .OrderBy(pair => Math.Abs(pair.item.Ordinal - _boundary.Ordinal))
            .FirstOrDefault();
        if (exact.item is not null)
        {
            return exact.index;
        }

        var fallback = snapshot
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair =>
                pair.item.Kind == CodexRunThreadItemKind.UserMessage &&
                pair.item.Ordinal == _boundary.Ordinal &&
                string.Equals(
                    pair.item.ActionRowStructuralFingerprint,
                    _boundary.ActionRowStructuralFingerprint,
                    StringComparison.Ordinal));
        return fallback.item is null ? -1 : fallback.index;
    }

    private bool IsHistoricalUserMessage(CodexRunOrderedThreadItem item)
    {
        var contentSignature = item.ContentSignature;
        var maximumBaselineOrdinal = _baseline.OrderedUserMessagesSafe
            .Select(baseline => baseline.Ordinal)
            .DefaultIfEmpty(-1)
            .Max();
        if (item.Ordinal > maximumBaselineOrdinal)
        {
            return false;
        }

        return _baseline.OrderedUserMessagesSafe.Any(baseline =>
            string.Equals(
                baseline.ContentSignature,
                contentSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                baseline.ActionRowStructuralFingerprint,
                item.ActionRowStructuralFingerprint,
                StringComparison.Ordinal));
    }

    private void ApplyOutputSlot(
        CodexRunOrderedThreadItem item,
        int slot,
        DateTimeOffset observedAtUtc)
    {
        var text = item.Text;
        var contentSignature = item.ContentSignature;
        if (slot >= _outputs.Count)
        {
            _outputs.Add(new TrackedOutput
            {
                SequenceIndex = slot,
                StructuralFingerprint = item.StructuralFingerprint,
                ParentStructuralFingerprint = item.ParentStructuralFingerprint,
                FirstObservedAtUtc = observedAtUtc,
                LastObservedAtUtc = observedAtUtc,
                Text = text,
                ContentSignature = contentSignature
            });
            LastNewOutputCount++;
            _lastNewOutputIndices.Add(slot);
            _lastSignatureChangedOutputIndices.Add(slot);
            return;
        }

        var tracked = _outputs[slot];
        var signatureChanged = !string.Equals(
            tracked.ContentSignature,
            contentSignature,
            StringComparison.Ordinal);
        var changed = !string.Equals(tracked.Text, text, StringComparison.Ordinal) ||
                      signatureChanged ||
                      !string.Equals(
                          tracked.StructuralFingerprint,
                          item.StructuralFingerprint,
                          StringComparison.Ordinal);
        tracked.SequenceIndex = slot;
        tracked.StructuralFingerprint = item.StructuralFingerprint;
        tracked.ParentStructuralFingerprint = item.ParentStructuralFingerprint;
        tracked.LastObservedAtUtc = observedAtUtc;
        tracked.Text = text;
        tracked.ContentSignature = contentSignature;
        if (changed)
        {
            LastChangedOutputCount++;
            _lastChangedOutputIndices.Add(slot);
        }

        if (signatureChanged)
        {
            _lastSignatureChangedOutputIndices.Add(slot);
        }
    }

    private static string GetContentSignature(CodexRunBlockObservation observation) =>
        string.IsNullOrWhiteSpace(observation.ContentSignature)
            ? ComputeContentSignature(observation.Text)
            : observation.ContentSignature;

    private TrackedOutput? FindTracked(CodexRunBlockObservation observation)
    {
        var exact = _outputs.FirstOrDefault(output =>
            output.SequenceIndex == observation.SequenceIndex &&
            string.Equals(
                output.StructuralFingerprint,
                observation.StructuralFingerprint,
                StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        return _outputs.FirstOrDefault(output =>
            output.SequenceIndex == observation.SequenceIndex &&
            string.Equals(
                output.ParentStructuralFingerprint,
                observation.ParentStructuralFingerprint,
                StringComparison.Ordinal) &&
            HasStableTextOverlap(output.Text, observation.Text));
    }

    private bool IsHistorical(CodexRunBlockObservation observation)
    {
        return _baseline.OrderedBlocksSafe.Any(baseline =>
            baseline.SequenceIndex == observation.SequenceIndex &&
            string.Equals(
                baseline.ParentStructuralFingerprint,
                observation.ParentStructuralFingerprint,
                StringComparison.Ordinal) &&
            (string.Equals(
                 baseline.StructuralFingerprint,
                 observation.StructuralFingerprint,
                 StringComparison.Ordinal) ||
             HasStablePrefix(observation.Text, baseline.TextPrefixHash, baseline.TextLength)));
    }

    private static bool HasStableTextOverlap(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length <= second.Length ? second : first;
        return shorter.Length >= 8 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static bool HasStablePrefix(string text, string expectedHash, int minimumLength)
    {
        if (text.Length < minimumLength || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        return string.Equals(
            ComputeTextPrefixHash(text),
            expectedHash,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeTextPrefixHash(string? text)
    {
        var prefix = text ?? string.Empty;
        if (prefix.Length > 128)
        {
            prefix = prefix[..128];
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix))).ToLowerInvariant();
    }

    public static string ComputeContentSignature(string? text)
    {
        var normalized = NormalizeContent(text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public static int CountLines(string? text)
    {
        var normalized = NormalizeContent(text);
        return normalized.Length == 0 ? 0 : normalized.Count(character => character == '\n') + 1;
    }

    private static string NormalizeContent(string? text) =>
        Regex.Replace(
                (text ?? string.Empty)
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n'),
                @"[ \t]+",
                " ")
            .Trim();
}

public static class CodexRunResultRenderer
{
    public static string Render(IReadOnlyList<CodexRunOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var ordered = outputs
            .Where(output => !string.IsNullOrWhiteSpace(output.Text))
            .OrderBy(output => output.SequenceIndex)
            .ToList();
        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        if (ordered.Count == 1)
        {
            return ordered[0].Text;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < ordered.Count; index++)
        {
            if (index > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---");
                builder.AppendLine();
            }

            builder.Append("[Codex Output ");
            builder.Append(index + 1);
            builder.Append('/');
            builder.Append(ordered.Count);
            builder.AppendLine("]");
            builder.Append(ordered[index].Text);
        }

        return builder.ToString();
    }
}
