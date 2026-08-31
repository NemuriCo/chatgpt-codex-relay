using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using BlueRelay.Diagnostics;

namespace BlueRelay.Services.Desktop;

public interface ICodexRunObserver
{
    bool IsWindowObserved(IntPtr windowHandle);

    bool TryStart(CodexRunReceipt receipt, out CodexRunObserverHandle? handle);

    void Stop(CodexRunObserverHandle handle);

    void StopAll();
}

public sealed class CodexRunObserverHandle : IDisposable
{
    private readonly Action _cancel;
    private int _cancelled;

    internal CodexRunObserverHandle(
        CodexRunReceipt receipt,
        Task<CodexRunResult> completion,
        Action cancel)
    {
        Receipt = receipt;
        Completion = completion;
        _cancel = cancel;
    }

    public CodexRunReceipt Receipt { get; }

    public Task<CodexRunResult> Completion { get; }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) == 0)
        {
            _cancel();
        }
    }

    public void Dispose() => Cancel();
}

public sealed class WindowsCodexRunObserver : ICodexRunObserver
{
    private readonly CodexRunObserverOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (CodexRunObserverHandle Handle, CancellationTokenSource Cancellation)> _activeByRun = [];
    private readonly Dictionary<IntPtr, Guid> _activeByWindow = [];

    public WindowsCodexRunObserver(CodexRunObserverOptions? options = null)
    {
        _options = (options ?? CodexRunObserverOptions.Default).Validate();
    }

    public bool IsWindowObserved(IntPtr windowHandle)
    {
        lock (_gate)
        {
            return windowHandle != IntPtr.Zero && _activeByWindow.ContainsKey(windowHandle);
        }
    }

    public bool TryStart(CodexRunReceipt receipt, out CodexRunObserverHandle? handle)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        handle = null;
        if (receipt.RunId == Guid.Empty ||
            receipt.WindowHandle == IntPtr.Zero ||
            receipt.ProcessId <= 0 ||
            !receipt.PreSendBaseline.IsAvailable)
        {
            return false;
        }

        lock (_gate)
        {
            if (_activeByWindow.ContainsKey(receipt.WindowHandle) ||
                _activeByRun.ContainsKey(receipt.RunId))
            {
                return false;
            }

            var cancellation = new CancellationTokenSource();
            var completion = Task.Run(
                () => ObserveAsync(receipt, cancellation.Token),
                CancellationToken.None);
            var localHandle = new CodexRunObserverHandle(
                receipt,
                completion,
                cancellation.Cancel);
            handle = localHandle;
            _activeByRun[receipt.RunId] = (localHandle, cancellation);
            _activeByWindow[receipt.WindowHandle] = receipt.RunId;
            _ = RemoveWhenCompleteAsync(receipt, localHandle, cancellation, completion);
            return true;
        }
    }

    public void Stop(CodexRunObserverHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Cancel();
    }

    public void StopAll()
    {
        (CodexRunObserverHandle Handle, CancellationTokenSource Cancellation)[] active;
        lock (_gate)
        {
            active = _activeByRun.Values.ToArray();
        }

        foreach (var item in active)
        {
            item.Handle.Cancel();
        }
    }

    private async Task RemoveWhenCompleteAsync(
        CodexRunReceipt receipt,
        CodexRunObserverHandle handle,
        CancellationTokenSource cancellation,
        Task<CodexRunResult> completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // ObserveAsync converts expected failures to CodexRunResult. This
            // guard prevents an unexpected worker exception from becoming
            // unobserved while the active entry is being removed.
        }
        finally
        {
            lock (_gate)
            {
                if (_activeByRun.TryGetValue(receipt.RunId, out var active) &&
                    ReferenceEquals(active.Handle, handle))
                {
                    _activeByRun.Remove(receipt.RunId);
                    if (_activeByWindow.TryGetValue(receipt.WindowHandle, out var runId) &&
                        runId == receipt.RunId)
                    {
                        _activeByWindow.Remove(receipt.WindowHandle);
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task<CodexRunResult> ObserveAsync(
        CodexRunReceipt receipt,
        CancellationToken cancellationToken)
    {
        StartupDiagnostics.Write(
            $"codex_run_observer_started runId={receipt.RunId} " +
            $"workstreamId={receipt.WorkstreamId} taskId={receipt.TaskId} " +
            $"generation={receipt.Generation} hwnd={receipt.WindowHandle.ToInt64()} " +
            $"pid={receipt.ProcessId} baselineCount={receipt.PreSendBaseline.AssistantBlockCount}");

        var accumulator = new CodexRunOutputAccumulator(receipt.PreSendBaseline);
        var completionTracker = new CodexRunCompletionTracker(_options.QuietWindow);
        var snapshotDiagnostics = new SnapshotDiagnosticThrottle();
        var candidateDiagnosticKeys = new HashSet<string>(StringComparer.Ordinal);
        StartupDiagnostics.Write(
            $"codex_run_state_transition runId={receipt.RunId} state={completionTracker.State}");

        var deadline = DateTimeOffset.UtcNow + _options.Timeout;
        DateTimeOffset? firstOutputAtUtc = null;
        DateTimeOffset? lastOutputChangeAtUtc = null;
        var lastControlState = CodexRunControlState.Unknown;
        var lastCompletionState = completionTracker.State;
        var boundaryLogged = false;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await StaAutomationWorker.RunAsync(
                    () => CodexRunSnapshotProbe.Capture(
                        receipt,
                        cancellationToken,
                        candidateDiagnosticKeys),
                    ThreadPriority.BelowNormal,
                    "BlueRelay Codex Run Observer").ConfigureAwait(false);

                if (!snapshot.WindowValid)
                {
                    StartupDiagnostics.Write(
                        $"codex_run_state_transition runId={receipt.RunId} " +
                        "state=ObserverUnavailable reason=window_changed");
                    return CodexRunResult.Failure(
                        receipt,
                        CodexRunCompletionMode.ObserverUnavailable,
                        "codex_run_window_changed",
                        "The original Codex window is no longer available.",
                        accumulator.SnapshotOutputs(),
                        isPartial: accumulator.SnapshotOutputs().Count > 0,
                        completedAtUtc: DateTimeOffset.UtcNow);
                }

                var observedAtUtc = DateTimeOffset.UtcNow;
                var reconciliation = accumulator.ApplyOrderedThreadSnapshot(
                    snapshot.OrderedThreadItems,
                    snapshot.ThreadOrderMethod,
                    observedAtUtc,
                    receipt.TaskId,
                    receipt.Generation,
                    receipt.CurrentTaskSemanticAnchors);
                var outputs = reconciliation.OutputsSafe;
                snapshotDiagnostics.Write(receipt.RunId, snapshot, reconciliation, observedAtUtc);

                if (!boundaryLogged && reconciliation.BoundaryConfirmed)
                {
                    boundaryLogged = true;
                    var boundary = reconciliation.Boundary!;
                    StartupDiagnostics.Write(
                        $"codex_run_boundary runId={receipt.RunId} confirmed=True " +
                        $"matchedCurrentTask={boundary.MatchedCurrentTask} " +
                        $"semanticAnchorCount={boundary.SemanticAnchorCount} " +
                        $"matchedAnchorCount={boundary.MatchedAnchorCount} " +
                        $"boundaryOrdinal={boundary.Ordinal} " +
                        $"threadOrderMethod={Sanitize(reconciliation.ThreadOrderMethod)}");
                }

                if (reconciliation.BoundarySuperseded)
                {
                    StartupDiagnostics.Write(
                        $"codex_run_boundary runId={receipt.RunId} confirmed=True " +
                        "superseded=True reason=new_user_message_after_boundary");
                    return CodexRunResult.Failure(
                        receipt,
                        CodexRunCompletionMode.BoundarySuperseded,
                        "codex_run_boundary_superseded",
                        "A new user message appeared before the current Codex run completed.",
                        outputs,
                        isPartial: outputs.Count > 0,
                        completedAtUtc: DateTimeOffset.UtcNow);
                }

                if (accumulator.LastNewOutputCount > 0 || accumulator.LastChangedOutputCount > 0)
                {
                    firstOutputAtUtc ??= observedAtUtc;
                    lastOutputChangeAtUtc = observedAtUtc;
                }

                if (snapshot.RunControlState != lastControlState)
                {
                    if (lastControlState == CodexRunControlState.Running &&
                        snapshot.RunControlState == CodexRunControlState.NotRunning)
                    {
                        WriteIdleProbe(receipt.RunId, snapshot.ComposerButtons);
                    }

                    lastControlState = snapshot.RunControlState;
                    StartupDiagnostics.Write(
                        $"codex_run_control runId={receipt.RunId} " +
                        $"state={snapshot.RunControlState} " +
                        $"composerAvailable={snapshot.ComposerAvailable}");
                }

                var decision = completionTracker.Observe(
                    snapshot.RunControlState,
                    outputs.Count,
                    accumulator.LastNewOutputCount,
                    accumulator.LastChangedOutputCount,
                    observedAtUtc);
                if (decision.State != lastCompletionState)
                {
                    lastCompletionState = decision.State;
                    StartupDiagnostics.Write(
                        $"codex_run_state_transition runId={receipt.RunId} " +
                        $"state={decision.State} quietMs={decision.QuietFor.TotalMilliseconds:0}");
                }

                if (accumulator.LastNewOutputCount > 0 || accumulator.LastChangedOutputCount > 0)
                {
                    foreach (var outputIndex in accumulator.LastNewOutputIndices
                                 .Concat(accumulator.LastChangedOutputIndices)
                                 .Distinct()
                                 .OrderBy(index => index))
                    {
                        var ordinal = outputIndex < reconciliation.AssistantOrdinalsAfterBoundarySafe.Count
                            ? reconciliation.AssistantOrdinalsAfterBoundarySafe[outputIndex]
                            : -1;
                        var item = snapshot.OrderedThreadItems.FirstOrDefault(threadItem =>
                            threadItem.Kind == CodexRunThreadItemKind.AssistantOutput &&
                            threadItem.Ordinal == ordinal);
                        StartupDiagnostics.Write(
                            $"codex_run_output_observed runId={receipt.RunId} " +
                            $"index={outputIndex} " +
                            $"contentLength={item?.TextLength ?? 0} " +
                            $"lineCount={item?.LineCount ?? 0} " +
                            $"signatureChanged={accumulator.LastSignatureChangedOutputIndices.Contains(outputIndex)}");
                    }

                    StartupDiagnostics.Write(
                        $"codex_run_output_observed runId={receipt.RunId} " +
                        $"assistantBlockCount={snapshot.Blocks.Count} " +
                        $"newOutputCount={accumulator.LastNewOutputCount} " +
                        $"changedOutputCount={accumulator.LastChangedOutputCount} " +
                        $"totalOutputCount={outputs.Count} " +
                        $"assistantOrdinalsAfterBoundary={string.Join(',', reconciliation.AssistantOrdinalsAfterBoundarySafe)}");
                }

                var quietFor = lastOutputChangeAtUtc is { } lastChange
                    ? observedAtUtc - lastChange
                    : TimeSpan.Zero;
                var fallbackComplete = !completionTracker.RunningObserved &&
                                       snapshot.RunControlState == CodexRunControlState.NotRunning &&
                                       firstOutputAtUtc is { } firstOutput &&
                                       observedAtUtc - firstOutput >= _options.FallbackQuietWindow &&
                                       quietFor >= _options.QuietWindow &&
                                       outputs.Count > 0;
                if (decision.IsComplete || fallbackComplete)
                {
                    var completionMode = decision.IsComplete
                        ? CodexRunCompletionMode.NativeRunControl
                        : CodexRunCompletionMode.FallbackQuietWindow;
                    if (outputs.Count == 0)
                    {
                        // Keep the guard at the completion boundary so a
                        // zero-output run can never be marked ready.
                        StartupDiagnostics.Write(
                            $"codex_run_capture runId={receipt.RunId} success=false " +
                            "outputCount=0 reason=no_outputs");
                        return CodexRunResult.Failure(
                            receipt,
                            CodexRunCompletionMode.NoOutputs,
                            "codex_run_no_outputs",
                            "Codex completed without a capturable assistant response.",
                            completedAtUtc: DateTimeOffset.UtcNow);
                    }

                    StartupDiagnostics.Write(
                        $"codex_run_state_transition runId={receipt.RunId} " +
                        $"state=Completed mode={completionMode} outputCount={outputs.Count}");
                    var capture = await StaAutomationWorker.RunAsync(
                        () => CodexRunSnapshotProbe.CaptureOutputs(
                            receipt,
                            outputs,
                            cancellationToken,
                            accumulator.Boundary),
                        ThreadPriority.BelowNormal,
                        "BlueRelay Codex Run Capture").ConfigureAwait(false);
                    if (!capture.IsComplete || capture.Outputs.Count == 0)
                    {
                        StartupDiagnostics.Write(
                            $"codex_run_capture runId={receipt.RunId} success=false " +
                            $"outputCount={capture.Outputs.Count} " +
                            $"captureComplete={capture.IsComplete}");
                        return CodexRunResult.Failure(
                            receipt,
                            CodexRunCompletionMode.CaptureIncomplete,
                            capture.Outputs.Count == 0 ? "codex_run_no_outputs" : "codex_run_capture_incomplete",
                            capture.Outputs.Count == 0
                                ? "Codex completed without a capturable assistant response."
                                : "Codex output capture was incomplete; BlueRelay did not mark the task ready.",
                            capture.Outputs,
                            isPartial: capture.Outputs.Count > 0,
                            completedAtUtc: DateTimeOffset.UtcNow);
                    }

                    var captureMethodSummary = string.Join(
                        ",",
                        capture.Outputs
                            .GroupBy(output => output.CaptureMethod)
                            .OrderBy(group => group.Key)
                            .Select(group => $"{group.Key}={group.Count()}"));
                    StartupDiagnostics.Write(
                        $"codex_run_capture runId={receipt.RunId} success=true " +
                        $"outputCount={capture.Outputs.Count} " +
                        $"captureMethodSummary={captureMethodSummary}");
                    foreach (var output in capture.Outputs.OrderBy(output => output.SequenceIndex))
                    {
                        StartupDiagnostics.Write(
                            $"codex_run_capture runId={receipt.RunId} " +
                            $"index={output.SequenceIndex} " +
                            $"method={(output.CaptureMethod == CodexRunCaptureMethod.NativeCopy ? "native_copy" : "fallback")} " +
                            $"contentLength={output.Text.Length} " +
                            $"lineCount={CodexRunOutputAccumulator.CountLines(output.Text)}");
                    }
                    StartupDiagnostics.Write(
                        $"codex_run_completed runId={receipt.RunId} " +
                        $"outputCount={capture.Outputs.Count}");
                    return new CodexRunResult(
                        receipt.RunId,
                        receipt.ProjectId,
                        receipt.WorkstreamId,
                        receipt.TaskId,
                        receipt.Generation,
                        receipt.WindowHandle,
                        receipt.ProcessId,
                        receipt.StartedAtUtc,
                        DateTimeOffset.UtcNow,
                        completionMode,
                        capture.Outputs,
                        true,
                        "codex_run_completed",
                        string.Empty);
                }

                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            var timeoutOutputs = accumulator.SnapshotOutputs();
            StartupDiagnostics.Write(
                $"codex_run_state_transition runId={receipt.RunId} state=Timeout " +
                $"outputCount={timeoutOutputs.Count}");
            return CodexRunResult.Failure(
                receipt,
                CodexRunCompletionMode.Timeout,
                "codex_run_timeout",
                "Codex did not reach a confirmed completion state within the observer timeout.",
                timeoutOutputs,
                isPartial: timeoutOutputs.Count > 0,
                completedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            var outputs = accumulator.SnapshotOutputs();
            StartupDiagnostics.Write(
                $"codex_run_state_transition runId={receipt.RunId} state=Cancelled " +
                $"outputCount={outputs.Count}");
            return CodexRunResult.Failure(
                receipt,
                CodexRunCompletionMode.Cancelled,
                "codex_run_cancelled",
                "Codex run observation was cancelled.",
                outputs,
                isPartial: outputs.Count > 0,
                completedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            StartupDiagnostics.WriteException("Codex run observer", exception);
            var outputs = accumulator.SnapshotOutputs();
            return CodexRunResult.Failure(
                receipt,
                CodexRunCompletionMode.ObserverUnavailable,
                "codex_run_observer_failed",
                "BlueRelay could not continue observing the Codex run.",
                outputs,
                isPartial: outputs.Count > 0,
                completedAtUtc: DateTimeOffset.UtcNow);
        }
    }

    private static void WriteIdleProbe(
        Guid runId,
        IReadOnlyList<CodexSendButtonMetadata> buttons)
    {
        StartupDiagnostics.Write(
            $"codex_run_idle_probe runId={runId} buttonCount={buttons.Count}");
        for (var index = 0; index < buttons.Count; index++)
        {
            var candidate = buttons[index];
            StartupDiagnostics.Write(
                $"codex_run_idle_candidate runId={runId} candidateIndex={index} " +
                $"controlType={Sanitize(candidate.ControlType)} " +
                $"localizedControlType={Sanitize(candidate.LocalizedControlType)} " +
                $"automationId={Sanitize(candidate.AutomationId)} " +
                $"className={Sanitize(candidate.ClassName)} " +
                $"frameworkId={Sanitize(candidate.FrameworkId)} " +
                $"enabled={candidate.IsEnabled} offscreen={candidate.IsOffscreen} " +
                $"invokePattern={candidate.InvokePatternAvailable} " +
                $"bounds={FormatBounds(candidate.Bounds)} " +
                $"name={Sanitize(candidate.Name)}");
        }
    }

    private static string FormatBounds(UiAutomationBounds bounds) =>
        $"{bounds.Left:0.##},{bounds.Top:0.##},{bounds.Width:0.##},{bounds.Height:0.##}";

    private static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Replace(' ', '_').Replace('\t', '_').Replace('\r', '_').Replace('\n', '_');

    private static bool IsUiAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or
        InvalidOperationException or
        COMException or
        ExternalException;

    private sealed class SnapshotDiagnosticThrottle
    {
        private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);
        private string? _lastKey;
        private DateTimeOffset? _lastWrittenAtUtc;

        public void Write(
            Guid runId,
            CodexRunSnapshot snapshot,
            CodexRunReconciliation reconciliation,
            DateTimeOffset observedAtUtc)
        {
            var key = $"{snapshot.AssistantBlocks.Count}|{snapshot.UserMessages.Count}|" +
                      $"{snapshot.RunControlState}|{reconciliation.OutputsSafe.Count}|" +
                      $"{reconciliation.BoundaryConfirmed}|{reconciliation.BoundarySuperseded}";
            if (string.Equals(_lastKey, key, StringComparison.Ordinal) &&
                _lastWrittenAtUtc is { } lastWritten &&
                observedAtUtc - lastWritten < SummaryInterval)
            {
                return;
            }

            _lastKey = key;
            _lastWrittenAtUtc = observedAtUtc;
            StartupDiagnostics.Write(
                $"codex_run_snapshot runId={runId} " +
                $"assistantBlockCount={snapshot.Blocks.Count} " +
                $"assistantCandidateCount={snapshot.AssistantBlocks.Count} " +
                $"userCandidateCount={snapshot.UserMessages.Count} " +
                $"runControlState={snapshot.RunControlState} " +
                $"boundaryFound={reconciliation.BoundaryConfirmed} " +
                $"outputsAfterBoundary={reconciliation.AssistantOrdinalsAfterBoundarySafe.Count} " +
                $"outputCount={reconciliation.OutputsSafe.Count} " +
                $"threadOrderMethod={Sanitize(reconciliation.ThreadOrderMethod)} " +
                $"boundaryOrdinal={reconciliation.BoundaryOrdinal} " +
                $"assistantOrdinalsAfterBoundary={string.Join(',', reconciliation.AssistantOrdinalsAfterBoundarySafe)}");
        }
    }
}

internal sealed record CodexRunSnapshot(
    bool WindowValid,
    bool ComposerAvailable,
    CodexRunControlState RunControlState,
    IReadOnlyList<CodexRunBlockObservation> Blocks,
    IReadOnlyList<CodexAssistantBlockSnapshot> AssistantBlocks,
    IReadOnlyList<CodexUserMessageSnapshot> UserMessages,
    IReadOnlyList<CodexRunOrderedThreadItem> OrderedThreadItems,
    string ThreadOrderMethod,
    IReadOnlyList<CodexSendButtonMetadata> ComposerButtons);

internal sealed record CodexAssistantBlockSnapshot(
    CodexRunBlockObservation Observation,
    AutomationElement? CopyButton,
    CodexAssistantBlockDescriptor Descriptor);

internal sealed record CodexAssistantBlockDescriptor(
    string ContainerStructuralFingerprint,
    string ParentStructuralFingerprint,
    string ActionRowStructuralFingerprint,
    int ActionButtonCount,
    bool HasFeedbackAction,
    int AncestorDepth,
    IReadOnlyList<CodexRunParentMetadata> ParentWalk);

internal sealed record CodexUserMessageSnapshot(
    CodexRunThreadActionObservation Observation,
    AutomationElement CopyButton);

internal sealed record CodexThreadProjectionSnapshot(
    IReadOnlyList<CodexAssistantBlockSnapshot> AssistantBlocks,
    IReadOnlyList<CodexUserMessageSnapshot> UserMessages,
    IReadOnlyList<CodexRunOrderedThreadItem> OrderedItems,
    string OrderMethod);

internal sealed record CodexRunParentMetadata(
    int Depth,
    string ControlType,
    string AutomationId,
    string ClassName,
    string FrameworkId,
    UiAutomationBounds Bounds,
    int ChildCount,
    int SiblingCount);

internal sealed record CodexRunCopyCandidate(
    AutomationElement Element,
    CodexSendButtonMetadata Metadata,
    IReadOnlyList<CodexRunParentMetadata> ParentWalk);

internal sealed record CodexRunThreadScope(
    AutomationElement Element,
    bool IsFlexColumnReverse);

internal sealed record CodexRunCaptureResult(
    bool IsComplete,
    IReadOnlyList<CodexRunOutput> Outputs);

internal static class CodexRunSnapshotProbe
{
    private const int MaxWindowNodes = 3500;
    private const int MaxWindowDepth = 42;
    private const int MaxActionNodes = 768;
    private const int MaxActionDepth = 10;
    private const int MaxCopyAncestorDepth = 12;
    private const int MaxTextDepth = 16;
    private const int MaxTextLength = 1_000_000;
    private static readonly string[] CopyTokens = [
        "copy", "copy to clipboard", "复制", "复制到剪贴板", "拷贝"];
    private static readonly string[] FeedbackTokens = [
        "thumb", "like", "dislike", "upvote", "downvote", "feedback",
        "赞", "踩", "有用", "无用", "有帮助", "无帮助"];
    private static readonly string[] RunTokens = [
        "stop", "cancel", "停止", "取消", "generating", "生成中"];
    private static readonly string[] ExcludedTokens = [
        "composer", "prompt", "input", "textarea", "sidebar", "navigation",
        "terminal", "status", "toolbar", "menu", "dialog"];

    internal static CodexRunBaseline CaptureBaseline(
        AutomationElement window,
        AutomationElement composer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var projection = ScanThreadProjection(
            window,
            composer,
            cancellationToken,
            Guid.Empty,
            new HashSet<string>(StringComparer.Ordinal),
            emitDetailedDiagnostics: false);
        var blocks = projection.AssistantBlocks;
        StartupDiagnostics.Write(
            $"codex_run_baseline available=True assistantBlockCount={blocks.Count} " +
            $"userMessageCount={projection.UserMessages.Count} " +
            $"elapsedMs={stopwatch.ElapsedMilliseconds} diagnostics=summary");
        return new CodexRunBaseline(
            true,
            blocks.Count,
            blocks.Select((block, index) => new CodexRunBlockBaseline(
                block.Observation.SequenceIndex,
                block.Observation.StructuralFingerprint,
                block.Observation.ParentStructuralFingerprint,
                block.Observation.Text.Length,
                string.IsNullOrWhiteSpace(block.Observation.TextPrefixHash)
                    ? CodexRunOutputAccumulator.ComputeTextPrefixHash(block.Observation.Text)
                    : block.Observation.TextPrefixHash)).ToList(),
            projection.UserMessages.Select(user => new CodexRunUserMessageBaseline(
                user.Observation.SourceOrdinal,
                user.Observation.StructuralFingerprint,
                user.Observation.ParentStructuralFingerprint,
                user.Observation.ActionRowStructuralFingerprint,
                user.Observation.TextLength,
                CodexRunOutputAccumulator.ComputeTextPrefixHash(user.Observation.Text),
                user.Observation.ContentSignatureSafe)).ToList());
    }

    internal static CodexRunSnapshot Capture(
        CodexRunReceipt receipt,
        CancellationToken cancellationToken,
        ISet<string>? candidateDiagnosticKeys = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.WindowHandle == IntPtr.Zero ||
            receipt.ProcessId <= 0 ||
            !NativeMethods.IsWindow(receipt.WindowHandle))
        {
            return EmptySnapshot(windowValid: false);
        }

        AutomationElement window;
        try
        {
            window = AutomationElement.FromHandle(receipt.WindowHandle);
            if (window.Current.ProcessId != receipt.ProcessId)
            {
                return EmptySnapshot(windowValid: false);
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return EmptySnapshot(windowValid: false);
        }

        var composer = FindComposer(window, receipt, cancellationToken);
        var projection = ScanThreadProjection(
            window,
            composer,
            cancellationToken,
            receipt.RunId,
            candidateDiagnosticKeys ?? new HashSet<string>(StringComparer.Ordinal),
            emitDetailedDiagnostics: false);
        IReadOnlyList<CodexSendButtonMetadata> composerButtons = [];
        var controlState = CodexRunControlState.Unknown;
        if (composer is not null)
        {
            controlState = InspectRunControl(composer, cancellationToken, out composerButtons);
        }
        return new(
            true,
            composer is not null,
            controlState,
            projection.AssistantBlocks.Select(block => block.Observation).ToList(),
            projection.AssistantBlocks,
            projection.UserMessages,
            projection.OrderedItems,
            projection.OrderMethod,
            composerButtons);
    }

    internal static CodexRunCaptureResult CaptureOutputs(
        CodexRunReceipt receipt,
        IReadOnlyList<CodexRunOutput> trackedOutputs,
        CancellationToken cancellationToken,
        CodexRunBoundary? boundary = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Capture(receipt, cancellationToken);
        if (!snapshot.WindowValid)
        {
            return new(false, trackedOutputs);
        }

        var liveBoundaryOrdinal = FindCurrentBoundaryOrdinal(snapshot, boundary);
        var liveBlocks = snapshot.AssistantBlocks
            .Where(block => liveBoundaryOrdinal < 0 ||
                            block.Observation.SequenceIndex > liveBoundaryOrdinal)
            .ToList();
        var outputs = new List<CodexRunOutput>(trackedOutputs.Count);
        var complete = liveBlocks.Count > 0;
        var clipboardCaptured = WindowsCodexDesktopComposerInjector.ClipboardSnapshot.TryCapture(
            out var clipboardSnapshot,
            out var clipboardFormat,
            out var clipboardFailure);
        StartupDiagnostics.Write(
            $"codex_run_clipboard_snapshot runId={receipt.RunId} success={clipboardCaptured} " +
            $"format={Sanitize(clipboardFormat)} failure={Sanitize(clipboardFailure)}");

        try
        {
            foreach (var tracked in trackedOutputs.OrderBy(output => output.SequenceIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var live = FindMatchingBlock(tracked, liveBlocks);
                var text = live?.Observation.Text ?? tracked.Text;
                var captureMethod = CodexRunCaptureMethod.UiaPlainText;
                if (live is null)
                {
                    complete = false;
                }
                else if (clipboardCaptured && live.CopyButton is not null &&
                         TryCopyOnce(live.CopyButton, out var copiedText, cancellationToken))
                {
                    text = copiedText;
                    captureMethod = CodexRunCaptureMethod.NativeCopy;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    complete = false;
                }

                outputs.Add(tracked with
                {
                    Text = text,
                    CaptureMethod = captureMethod,
                    LastObservedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }
        finally
        {
            if (clipboardCaptured)
            {
                var restored = clipboardSnapshot.TryRestore();
                StartupDiagnostics.Write(
                    $"codex_run_clipboard_restore runId={receipt.RunId} success={restored}");
                complete &= restored;
            }
        }

        return new(complete && outputs.Count > 0, outputs);
    }

    private static CodexRunSnapshot EmptySnapshot(bool windowValid) =>
        new(windowValid, false, CodexRunControlState.Unknown, [], [], [], [], "", []);

    private static AutomationElement? FindComposer(
        AutomationElement window,
        CodexRunReceipt receipt,
        CancellationToken cancellationToken)
    {
        return FindComposerInView(window, receipt, TreeWalker.ControlViewWalker, cancellationToken) ??
               FindComposerInView(window, receipt, TreeWalker.RawViewWalker, cancellationToken);
    }

    private static AutomationElement? FindComposerInView(
        AutomationElement window,
        CodexRunReceipt receipt,
        TreeWalker walker,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((window, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxWindowNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            if (IsComposerMatch(element, receipt))
            {
                return element;
            }

            if (depth >= MaxWindowDepth)
            {
                continue;
            }

            foreach (var child in GetChildren(element, walker, 256))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    private static bool IsComposerMatch(AutomationElement element, CodexRunReceipt receipt)
    {
        try
        {
            var current = element.Current;
            var type = GetControlTypeName(current.ControlType);
            if (type is not ("Edit" or "Document" or "Custom") ||
                !current.IsEnabled || current.IsOffscreen ||
                current.BoundingRectangle.Width <= 0 ||
                current.BoundingRectangle.Height <= 0)
            {
                return false;
            }

            var automationIdMatches = !string.IsNullOrWhiteSpace(receipt.ComposerAutomationId) &&
                                      string.Equals(current.AutomationId, receipt.ComposerAutomationId, StringComparison.OrdinalIgnoreCase);
            var classMatches = !string.IsNullOrWhiteSpace(receipt.ComposerClassName) &&
                               string.Equals(current.ClassName, receipt.ComposerClassName, StringComparison.OrdinalIgnoreCase);
            var proseMirror = ContainsToken(current.ClassName, "ProseMirror");
            return IsChromium(current.FrameworkId) && (automationIdMatches || classMatches || proseMirror);
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return false;
        }
    }

    private static CodexRunControlState InspectRunControl(
        AutomationElement composer,
        CancellationToken cancellationToken,
        out IReadOnlyList<CodexSendButtonMetadata> buttons)
    {
        var candidates = new List<CodexSendButtonMetadata>();
        AutomationElement? ancestor = composer;
        for (var parentDepth = 0; parentDepth <= 4 && ancestor is not null; parentDepth++)
        {
            candidates.AddRange(FindButtons(ancestor, TreeWalker.ControlViewWalker, cancellationToken));
            candidates.AddRange(FindButtons(ancestor, TreeWalker.RawViewWalker, cancellationToken));
            ancestor = GetParent(ancestor);
        }

        buttons = candidates.Distinct().ToList();
        return buttons.Any(IsRunningButton)
            ? CodexRunControlState.Running
            : CodexRunControlState.NotRunning;
    }

    private static IReadOnlyList<CodexSendButtonMetadata> FindButtons(
        AutomationElement root,
        TreeWalker walker,
        CancellationToken cancellationToken)
    {
        var result = new List<CodexSendButtonMetadata>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxActionNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            try
            {
                var current = element.Current;
                if (current.ControlType == ControlType.Button)
                {
                    result.Add(CreateButtonMetadata(element, current));
                }

                if (depth < MaxActionDepth)
                {
                    foreach (var child in GetChildren(element, walker, 128))
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }
        }

        return result;
    }

    private static bool IsRunningButton(CodexSendButtonMetadata metadata)
    {
        var signal = GetButtonSignal(metadata);
        return metadata.ControlType.Equals("Button", StringComparison.OrdinalIgnoreCase) &&
               metadata.InvokePatternAvailable &&
               metadata.IsEnabled &&
               !metadata.IsOffscreen &&
               !metadata.Bounds.IsEmpty &&
               ContainsAny(signal, RunTokens);
    }

    private static CodexThreadProjectionSnapshot ScanThreadProjection(
        AutomationElement window,
        AutomationElement? composer,
        CancellationToken cancellationToken,
        Guid runId,
        ISet<string> candidateDiagnosticKeys,
        bool emitDetailedDiagnostics = true)
    {
        var threadScope = FindThreadScope(window, composer);
        var controlCandidates = FindCopyCandidates(
            threadScope.Element,
            composer,
            TreeWalker.ControlViewWalker,
            cancellationToken,
            runId,
            candidateDiagnosticKeys,
            emitDetailedDiagnostics);
        var projection = BuildThreadProjection(
            controlCandidates,
            composer,
            threadScope.IsFlexColumnReverse,
            cancellationToken);
        if (projection.AssistantBlocks.Count > 0 || projection.UserMessages.Count > 0)
        {
            return projection;
        }

        // Chromium/WebView2 may expose the same DOM button only in RawView.
        // Use it as a bounded fallback when ControlView did not yield a
        // trusted assistant block.
        var rawCandidates = FindCopyCandidates(
            threadScope.Element,
            composer,
            TreeWalker.RawViewWalker,
            cancellationToken,
            runId,
            candidateDiagnosticKeys,
            emitDetailedDiagnostics);
        projection = BuildThreadProjection(
            rawCandidates,
            composer,
            threadScope.IsFlexColumnReverse,
            cancellationToken);
        if (projection.AssistantBlocks.Count > 0 ||
            projection.UserMessages.Count > 0 ||
            IsSameElement(threadScope.Element, window))
        {
            return projection;
        }

        // The thread token is only a heuristic. If a particular Codex build
        // exposes a misleading scroll ancestor, retry the same classifier in
        // the window root rather than silently losing all assistant blocks.
        var windowControlCandidates = FindCopyCandidates(
            window,
            composer,
            TreeWalker.ControlViewWalker,
            cancellationToken,
            runId,
            candidateDiagnosticKeys,
            emitDetailedDiagnostics);
        projection = BuildThreadProjection(
            windowControlCandidates,
            composer,
            threadScope.IsFlexColumnReverse,
            cancellationToken);
        if (projection.AssistantBlocks.Count > 0 || projection.UserMessages.Count > 0)
        {
            return projection;
        }

        var windowRawCandidates = FindCopyCandidates(
            window,
            composer,
            TreeWalker.RawViewWalker,
            cancellationToken,
            runId,
            candidateDiagnosticKeys,
            emitDetailedDiagnostics);
        return BuildThreadProjection(
            windowRawCandidates,
            composer,
            threadScope.IsFlexColumnReverse,
            cancellationToken);
    }

    private static CodexThreadProjectionSnapshot BuildThreadProjection(
        IReadOnlyList<CodexRunCopyCandidate> candidates,
        AutomationElement? composer,
        bool isFlexColumnReverse,
        CancellationToken cancellationToken)
    {
        var assistantBlocks = BuildAssistantBlocks(candidates, composer, cancellationToken);
        var userMessages = BuildUserMessages(candidates, composer, cancellationToken);
        var observations = assistantBlocks
            .Select(block => new CodexRunThreadActionObservation(
                CodexRunThreadItemKind.AssistantOutput,
                block.Observation.StructuralFingerprint,
                block.Observation.ParentStructuralFingerprint,
                block.Observation.ActionRowStructuralFingerprint,
                "copy",
                block.Observation.Text,
                block.Observation.Bounds ?? new UiAutomationBounds(0, 0, 0, 0),
                block.Observation.SequenceIndex,
                block.Observation.ContentSignature,
                block.Observation.LineCount))
            .Concat(userMessages.Select(user => new CodexRunThreadActionObservation(
                CodexRunThreadItemKind.UserMessage,
                user.Observation.StructuralFingerprint,
                user.Observation.ParentStructuralFingerprint,
                user.Observation.ActionRowStructuralFingerprint,
                user.Observation.ActionDescriptor,
                user.Observation.Text,
                user.Observation.Bounds,
                user.Observation.SourceOrdinal,
                user.Observation.ContentSignature,
                user.Observation.LineCountSafe)))
            .ToList();
        var normalized = OrderedThreadActionProjection.NormalizeThreadOrder(
            observations,
            isFlexColumnReverse);
        var orderedAssistants = normalized.ItemsSafe
            .Where(item => item.Kind == CodexRunThreadItemKind.AssistantOutput)
            .Select(item =>
            {
                var block = assistantBlocks.FirstOrDefault(candidate =>
                    candidate.Observation.SequenceIndex == item.SourceOrdinal);
                return block is null
                    ? null
                    : block with
                    {
                        Observation = block.Observation with { SequenceIndex = item.Ordinal }
                    };
            })
            .Where(block => block is not null)
            .Cast<CodexAssistantBlockSnapshot>()
            .ToList();
        var orderedUsers = normalized.ItemsSafe
            .Where(item => item.Kind == CodexRunThreadItemKind.UserMessage)
            .Select(item =>
            {
                var user = userMessages.FirstOrDefault(candidate =>
                    candidate.Observation.SourceOrdinal == item.SourceOrdinal);
                return user is null
                    ? null
                    : user with
                    {
                        Observation = user.Observation with { SourceOrdinal = item.Ordinal }
                    };
            })
            .Where(user => user is not null)
            .Cast<CodexUserMessageSnapshot>()
            .ToList();
        return new(orderedAssistants, orderedUsers, normalized.ItemsSafe, normalized.Method);
    }

    private static CodexRunThreadScope FindThreadScope(
        AutomationElement window,
        AutomationElement? composer)
    {
        if (composer is null)
        {
            return new(window, false);
        }

        AutomationElement? nearestDocumentScope = null;
        var current = GetParent(composer);
        for (var depth = 1; depth <= 10 && current is not null; depth++)
        {
            try
            {
                var info = current.Current;
                var type = GetControlTypeName(info.ControlType);
                var signal = string.Join(
                    " ",
                    info.AutomationId,
                    info.ClassName,
                    info.Name,
                    info.LocalizedControlType,
                    info.HelpText);
                if (nearestDocumentScope is null &&
                    type is "Document" or "Pane" or "Custom" or "Group")
                {
                    nearestDocumentScope = current;
                }

                if (ContainsAny(
                        signal,
                        ["thread", "conversation", "message-list", "scroll-container", "scroll container"]))
                {
                    return new(
                        current,
                        ContainsAny(signal, ["flex-col-reverse", "flex_col_reverse", "flex flex-col-reverse"]));
                }
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }

            current = GetParent(current);
        }

        return new(nearestDocumentScope ?? window, false);
    }

    private static IReadOnlyList<CodexRunCopyCandidate> FindCopyCandidates(
        AutomationElement window,
        AutomationElement? composer,
        TreeWalker walker,
        CancellationToken cancellationToken,
        Guid runId,
        ISet<string> candidateDiagnosticKeys,
        bool emitDetailedDiagnostics)
    {
        var result = new List<CodexRunCopyCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        pending.Push((window, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < MaxWindowNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = pending.Pop();
            visited++;
            if (TryCreateCopyCandidate(
                    element,
                    composer,
                    emitDetailedDiagnostics,
                    out var candidate))
            {
                var key = BuildCandidateKey(candidate);
                if (seen.Add(key))
                {
                    result.Add(candidate);
                    if (emitDetailedDiagnostics)
                    {
                        WriteCopyCandidateDiagnostics(
                            runId,
                            result.Count - 1,
                            candidate,
                            candidateDiagnosticKeys);
                    }
                }
            }

            if (depth >= MaxWindowDepth)
            {
                continue;
            }

            var children = GetChildren(element, walker, 256);
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push((children[index], depth + 1));
            }
        }

        return result;
    }

    private static bool TryCreateCopyCandidate(
        AutomationElement element,
        AutomationElement? composer,
        bool includeParentWalk,
        out CodexRunCopyCandidate candidate)
    {
        candidate = null!;
        try
        {
            var current = element.Current;
            if (current.ControlType != ControlType.Button)
            {
                return false;
            }

            var metadata = CreateButtonMetadata(element, current);
            if (!ContainsAny(GetButtonSignal(metadata), CopyTokens))
            {
                return false;
            }

            if (composer is not null && IsDescendantOf(element, composer))
            {
                return false;
            }

            candidate = new CodexRunCopyCandidate(
                element,
                metadata,
                includeParentWalk ? BuildParentWalk(element) : []);
            return true;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return false;
        }
    }

    private static IReadOnlyList<CodexAssistantBlockSnapshot> BuildAssistantBlocks(
        IReadOnlyList<CodexRunCopyCandidate> candidates,
        AutomationElement? composer,
        CancellationToken cancellationToken)
    {
        var blocks = new List<CodexAssistantBlockSnapshot>();
        var seenContainers = new HashSet<string>(StringComparer.Ordinal);
        var actionInspectionCache = new Dictionary<AutomationElement, AssistantActionInspection>(
            AutomationElementReferenceComparer.Instance);
        foreach (var (candidate, candidateIndex) in candidates.Select((candidate, index) => (candidate, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindResponseContainer(
                    candidate,
                    composer,
                    cancellationToken,
                    out var container,
                    out var text,
                    out var descriptor,
                    actionInspectionCache))
            {
                continue;
            }

            var containerKey = string.Join(
                "|",
                descriptor.ContainerStructuralFingerprint,
                CodexRunOutputAccumulator.ComputeTextPrefixHash(text),
                FormatBounds(candidate.Metadata.Bounds));
            if (!seenContainers.Add(containerKey))
            {
                continue;
            }

            var observation = CreateObservation(
                container!,
                text,
                candidateIndex,
                candidate.Metadata.Bounds,
                descriptor.ActionRowStructuralFingerprint);
            blocks.Add(new CodexAssistantBlockSnapshot(
                observation,
                candidate.Element,
                descriptor));
        }

        return blocks;
    }

    private static IReadOnlyList<CodexUserMessageSnapshot> BuildUserMessages(
        IReadOnlyList<CodexRunCopyCandidate> candidates,
        AutomationElement? composer,
        CancellationToken cancellationToken)
    {
        var messages = new List<CodexUserMessageSnapshot>();
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (candidate, candidateIndex) in candidates.Select((candidate, index) => (candidate, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUserMessageCopyCandidate(candidate) ||
                !TryFindUserMessageContainer(
                    candidate,
                    composer,
                    cancellationToken,
                    out var observation))
            {
                continue;
            }
            observation = observation with { SourceOrdinal = candidateIndex };

            var key = string.Join(
                "|",
                observation.StructuralFingerprint,
                observation.ContentSignatureSafe,
                FormatBounds(observation.Bounds));
            if (seenMessages.Add(key))
            {
                messages.Add(new CodexUserMessageSnapshot(observation, candidate.Element));
            }
        }

        return messages;
    }

    private static bool IsUserMessageCopyCandidate(CodexRunCopyCandidate candidate)
    {
        var signal = GetButtonSignal(candidate.Metadata);
        return ContainsAny(
            signal,
            [
                "copy message",
                "copy user message",
                "复制消息",
                "复制用户消息",
                "拷贝消息",
                "拷贝用户消息"
            ]);
    }

    private static bool TryFindUserMessageContainer(
        CodexRunCopyCandidate candidate,
        AutomationElement? composer,
        CancellationToken cancellationToken,
        out CodexRunThreadActionObservation observation)
    {
        observation = null!;
        var current = GetParent(candidate.Element);
        for (var depth = 1; depth <= MaxCopyAncestorDepth && current is not null; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = current.Current;
                var controlType = GetControlTypeName(info.ControlType);
                var signal = string.Join(
                    " ",
                    info.AutomationId,
                    info.ClassName,
                    info.Name,
                    info.LocalizedControlType,
                    info.HelpText);
                var inComposer = composer is not null && IsDescendantOf(current, composer);
                var candidateType = controlType is
                    "Group" or "Custom" or "ListItem" or "DataItem" or "Document" or "Text" or "Pane" or "Section";
                if (!candidateType || inComposer || ContainsAny(signal, ExcludedTokens))
                {
                    current = GetParent(current);
                    continue;
                }

                var actionRow = GetParent(candidate.Element);
                var actionButtons = actionRow is null
                    ? []
                    : FindButtons(actionRow, TreeWalker.ControlViewWalker, cancellationToken)
                        .Concat(FindButtons(actionRow, TreeWalker.RawViewWalker, cancellationToken))
                        .Distinct()
                        .Where(IsVisibleInvokableButton)
                        .ToList();
                if (!actionButtons.Any(button => IsUserMessageCopyButton(button)))
                {
                    current = GetParent(current);
                    continue;
                }

                if (!TryReadAssistantBody(current, candidate.Element, cancellationToken, out var text))
                {
                    current = GetParent(current);
                    continue;
                }

                var fingerprint = BuildStructuralFingerprint(current, out var parentFingerprint);
                var actionRowFingerprint = actionRow is null
                    ? string.Empty
                    : BuildStructuralFingerprint(actionRow, out _);
                observation = new CodexRunThreadActionObservation(
                    CodexRunThreadItemKind.UserMessage,
                    fingerprint,
                    parentFingerprint,
                    actionRowFingerprint,
                    "copy-message",
                    text,
                    new UiAutomationBounds(
                        candidate.Metadata.Bounds.Left,
                        candidate.Metadata.Bounds.Top,
                        candidate.Metadata.Bounds.Width,
                        candidate.Metadata.Bounds.Height),
                    0,
                    CodexRunOutputAccumulator.ComputeContentSignature(text),
                    CodexRunOutputAccumulator.CountLines(text));
                return true;
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }

            current = GetParent(current!);
        }

        return false;
    }

    private static bool IsUserMessageCopyButton(CodexSendButtonMetadata button) =>
        IsVisibleInvokableButton(button) &&
        ContainsAny(
            GetButtonSignal(button),
            ["copy message", "copy user message", "复制消息", "复制用户消息", "拷贝消息", "拷贝用户消息"]);

    private static bool TryFindResponseContainer(
        CodexRunCopyCandidate candidate,
        AutomationElement? composer,
        CancellationToken cancellationToken,
        out AutomationElement? container,
        out string text,
        out CodexAssistantBlockDescriptor descriptor,
        IDictionary<AutomationElement, AssistantActionInspection> actionInspectionCache)
    {
        container = null;
        text = string.Empty;
        descriptor = null!;
        var current = GetParent(candidate.Element);
        for (var depth = 1; depth <= MaxCopyAncestorDepth && current is not null; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = current.Current;
                var controlType = GetControlTypeName(info.ControlType);
                var signal = string.Join(
                    " ",
                    info.AutomationId,
                    info.ClassName,
                    info.Name,
                    info.LocalizedControlType,
                    info.HelpText);
                var inComposer = composer is not null && IsDescendantOf(current, composer);
                var inThreadScope = !inComposer && !ContainsAny(signal, ExcludedTokens);
                var candidateType = controlType is
                    "Group" or "Custom" or "ListItem" or "DataItem" or "Document" or "Text" or "Pane" or "Section";
                if (!candidateType || !inThreadScope)
                {
                    current = GetParent(current);
                    continue;
                }

                var action = InspectAssistantActionStructure(
                    candidate.Element,
                    current,
                    cancellationToken,
                    actionInspectionCache);
                if (!action.HasCopyButton)
                {
                    current = GetParent(current);
                    continue;
                }

                if (!TryReadAssistantBody(current, candidate.Element, cancellationToken, out var candidateText))
                {
                    current = GetParent(current);
                    continue;
                }

                var structure = new CodexAssistantActionStructure(
                    inThreadScope,
                    inComposer,
                    action.HasCopyButton,
                    action.ActionButtonCount,
                    action.HasFeedbackAction,
                    HasBodyContent: true);
                if (!CodexAssistantBlockClassifier.IsTrustedAssistantBlock(structure))
                {
                    current = GetParent(current);
                    continue;
                }

                var baseFingerprint = BuildStructuralFingerprint(current, out var parentFingerprint);
                descriptor = new CodexAssistantBlockDescriptor(
                    baseFingerprint,
                    parentFingerprint,
                    action.ActionRowStructuralFingerprint,
                    action.ActionButtonCount,
                    action.HasFeedbackAction,
                    depth,
                    candidate.ParentWalk);
                container = current;
                text = candidateText;
                return true;
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }

            current = GetParent(current!);
        }

        return false;
    }

    private static AssistantActionInspection InspectAssistantActionStructure(
        AutomationElement copyButton,
        AutomationElement ancestor,
        CancellationToken cancellationToken,
        IDictionary<AutomationElement, AssistantActionInspection> cache)
    {
        if (cache.TryGetValue(ancestor, out var cached))
        {
            return cached;
        }

        var buttons = FindButtons(ancestor, TreeWalker.ControlViewWalker, cancellationToken)
            .Concat(FindButtons(ancestor, TreeWalker.RawViewWalker, cancellationToken))
            .Distinct()
            .Where(IsVisibleInvokableButton)
            .ToList();
        var hasCopyButton = buttons.Any(button => ContainsAny(GetButtonSignal(button), CopyTokens));
        var hasFeedbackAction = buttons.Any(button => ContainsAny(GetButtonSignal(button), FeedbackTokens));
        var actionRowFingerprint = string.Empty;
        var actionRow = GetParent(copyButton);
        if (actionRow is not null)
        {
            actionRowFingerprint = BuildStructuralFingerprint(actionRow, out _);
        }

        var inspection = new AssistantActionInspection(
            hasCopyButton,
            buttons.Count,
            hasFeedbackAction,
            actionRowFingerprint);
        cache[ancestor] = inspection;
        return inspection;
    }

    private static bool IsVisibleInvokableButton(CodexSendButtonMetadata button) =>
        button.ControlType.Equals("Button", StringComparison.OrdinalIgnoreCase) &&
        button.InvokePatternAvailable &&
        button.IsEnabled &&
        !button.IsOffscreen &&
        !button.Bounds.IsEmpty;

    private static bool TryReadAssistantBody(
        AutomationElement container,
        AutomationElement copyButton,
        CancellationToken cancellationToken,
        out string text)
    {
        var parts = new List<string>();
        CollectProjectedText(container, copyButton, 0, parts, cancellationToken);
        var distinctParts = parts
            .Select(NormalizeProjectedText)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        text = string.Join("\n", distinctParts);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static void CollectProjectedText(
        AutomationElement element,
        AutomationElement copyButton,
        int depth,
        ICollection<string> parts,
        CancellationToken cancellationToken)
    {
        if (depth > MaxTextDepth || IsSameElement(element, copyButton))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var current = element.Current;
            if (current.ControlType == ControlType.Button)
            {
                return;
            }

            var children = GetChildren(element, TreeWalker.RawViewWalker, 256);
            var before = parts.Count;
            foreach (var child in children)
            {
                if (IsButtonElement(child))
                {
                    continue;
                }

                CollectProjectedText(child, copyButton, depth + 1, parts, cancellationToken);
            }

            if (parts.Count > before)
            {
                return;
            }

            if (TryReadText(element, out var text) &&
                !string.IsNullOrWhiteSpace(text) &&
                !IsActionOnlyText(text))
            {
                parts.Add(text);
                return;
            }

            var type = GetControlTypeName(current.ControlType);
            if (type is ("Text" or "Document" or "Custom") &&
                !string.IsNullOrWhiteSpace(current.Name) &&
                !IsActionOnlyText(current.Name))
            {
                parts.Add(current.Name);
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }
    }

    private static bool IsButtonElement(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType == ControlType.Button;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return true;
        }
    }

    private static string NormalizeProjectedText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static bool IsActionOnlyText(string value)
    {
        var remaining = value.ToLowerInvariant();
        foreach (var token in new[]
                 {
                     "copy", "copy to clipboard", "thumbs up", "thumbs down", "like", "dislike",
                     "feedback", "复制", "复制到剪贴板", "拷贝", "赞", "踩", "有用", "无用",
                     "有帮助", "无帮助"
                 })
        {
            remaining = remaining.Replace(token, string.Empty, StringComparison.Ordinal);
        }

        return remaining.All(character => char.IsWhiteSpace(character) || char.IsPunctuation(character));
    }

    private static CodexRunBlockObservation CreateObservation(
        AutomationElement container,
        string text,
        int sequenceIndex,
        UiAutomationBounds bounds,
        string actionRowStructuralFingerprint)
    {
        var fingerprint = BuildStructuralFingerprint(container, out var parentFingerprint);
        return new CodexRunBlockObservation(
            sequenceIndex,
            fingerprint,
            parentFingerprint,
            text,
            HasNativeCopy: true,
            CodexRunOutputAccumulator.ComputeTextPrefixHash(text),
            bounds,
            actionRowStructuralFingerprint,
            CodexRunOutputAccumulator.ComputeContentSignature(text),
            CodexRunOutputAccumulator.CountLines(text));
    }

    private static string BuildStructuralFingerprint(
        AutomationElement element,
        out string parentFingerprint)
    {
        var parts = new List<string>();
        var current = element;
        for (var depth = 0; depth < 5 && current is not null; depth++)
        {
            if (!TryGetStableMetadata(current, out var metadata))
            {
                break;
            }

            parts.Add(metadata);
            current = GetParent(current);
        }

        parentFingerprint = parts.Count > 1
            ? HashFingerprint(string.Join("/", parts.Skip(1)))
            : string.Empty;
        return HashFingerprint(string.Join("/", parts));
    }

    private static bool TryGetStableMetadata(
        AutomationElement element,
        out string metadata)
    {
        metadata = string.Empty;
        try
        {
            var current = element.Current;
            metadata = string.Join(
                "|",
                GetControlTypeName(current.ControlType),
                current.AutomationId ?? string.Empty,
                current.ClassName ?? string.Empty,
                current.FrameworkId ?? string.Empty);
            return true;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return false;
        }
    }

    private static CodexAssistantBlockSnapshot? FindMatchingBlock(
        CodexRunOutput tracked,
        IReadOnlyList<CodexAssistantBlockSnapshot> liveBlocks)
    {
        if (tracked.SequenceIndex < 0 || tracked.SequenceIndex >= liveBlocks.Count)
        {
            return null;
        }

        // The post-boundary slot is the message instance identity. Structural
        // metadata is only a weak rerender hint and must not displace a slot.
        return liveBlocks[tracked.SequenceIndex];
    }

    private static int FindCurrentBoundaryOrdinal(
        CodexRunSnapshot snapshot,
        CodexRunBoundary? boundary)
    {
        if (boundary is null)
        {
            return -1;
        }

        var current = snapshot.OrderedThreadItems
            .Where(item => item.Kind == CodexRunThreadItemKind.UserMessage)
            .LastOrDefault(item => string.Equals(
                item.ContentSignature,
                boundary.ContentSignature,
                StringComparison.Ordinal));
        return current is null ? boundary.Ordinal : current.Ordinal;
    }

    private static bool HasTextOverlap(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length <= second.Length ? second : first;
        return shorter.Length >= 8 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static bool TryCopyOnce(
        AutomationElement copyButton,
        out string text,
        CancellationToken cancellationToken)
    {
        text = string.Empty;
        try
        {
            var beforeSequence = NativeMethods.GetClipboardSequenceNumber();
            var beforeText = TryReadClipboardText(out var previousText) ? previousText : null;
            if (!TryGetInvokePattern(copyButton, out var invokePattern) || invokePattern is null)
            {
                return false;
            }

            invokePattern.Invoke();
            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.0);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = NativeMethods.GetClipboardSequenceNumber();
                if (TryReadClipboardText(out var currentText) &&
                    !string.IsNullOrWhiteSpace(currentText) &&
                    (sequence != beforeSequence || !string.Equals(currentText, beforeText, StringComparison.Ordinal)))
                {
                    text = currentText;
                    return true;
                }

                Thread.Sleep(50);
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception) || exception is ExternalException)
        {
        }

        return false;
    }

    private static bool TryReadClipboardText(out string text)
    {
        text = string.Empty;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return false;
            }

            text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadText(AutomationElement element, out string text)
    {
        text = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern)
            {
                text = LimitText(textPattern.DocumentRange.GetText(-1));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) &&
                patternObject is ValuePattern valuePattern)
            {
                text = LimitText(valuePattern.Current.Value);
                return !string.IsNullOrWhiteSpace(text);
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        return false;
    }

    private static string LimitText(string? text)
    {
        text ??= string.Empty;
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    private static CodexSendButtonMetadata CreateButtonMetadata(
        AutomationElement element,
        AutomationElement.AutomationElementInformation current)
    {
        var invokeAvailable = TryGetInvokePattern(element, out _);
        var bounds = current.BoundingRectangle;
        return new CodexSendButtonMetadata(
            GetControlTypeName(current.ControlType),
            current.LocalizedControlType ?? string.Empty,
            current.AutomationId ?? string.Empty,
            current.ClassName ?? string.Empty,
            current.FrameworkId ?? string.Empty,
            current.IsEnabled,
            current.IsOffscreen,
            invokeAvailable,
            false,
            new UiAutomationBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            current.Name ?? string.Empty,
            current.HelpText ?? string.Empty);
    }

    private static string GetButtonSignal(CodexSendButtonMetadata metadata) =>
        string.Join(
            " ",
            metadata.Name,
            metadata.LocalizedControlType,
            metadata.AutomationId,
            metadata.ClassName,
            metadata.HelpText);

    private static bool TryGetInvokePattern(
        AutomationElement element,
        out InvokePattern? invokePattern)
    {
        invokePattern = null;
        try
        {
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject))
            {
                return false;
            }

            invokePattern = patternObject as InvokePattern;
            return invokePattern is not null;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return false;
        }
    }

    private static AutomationElement? GetParent(AutomationElement element)
    {
        try
        {
            return TreeWalker.RawViewWalker.GetParent(element);
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return null;
        }
    }

    private static IReadOnlyList<AutomationElement> GetChildren(
        AutomationElement element,
        TreeWalker walker,
        int maximum)
    {
        var children = new List<AutomationElement>();
        try
        {
            var child = walker.GetFirstChild(element);
            while (child is not null && children.Count < maximum)
            {
                children.Add(child);
                child = walker.GetNextSibling(child);
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        return children;
    }

    private static IReadOnlyList<CodexRunParentMetadata> BuildParentWalk(AutomationElement element)
    {
        var result = new List<CodexRunParentMetadata>();
        var current = GetParent(element);
        for (var depth = 1; depth <= MaxCopyAncestorDepth && current is not null; depth++)
        {
            try
            {
                var info = current.Current;
                var parent = GetParent(current);
                var children = GetChildren(current, TreeWalker.RawViewWalker, 256);
                var siblingCount = parent is null
                    ? 0
                    : GetChildren(parent, TreeWalker.RawViewWalker, 256).Count;
                result.Add(new CodexRunParentMetadata(
                    depth,
                    GetControlTypeName(info.ControlType),
                    info.AutomationId ?? string.Empty,
                    info.ClassName ?? string.Empty,
                    info.FrameworkId ?? string.Empty,
                    new UiAutomationBounds(
                        info.BoundingRectangle.Left,
                        info.BoundingRectangle.Top,
                        info.BoundingRectangle.Width,
                        info.BoundingRectangle.Height),
                    children.Count,
                    siblingCount));
                current = parent;
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
                break;
            }
        }

        return result;
    }

    private static string BuildCandidateKey(CodexRunCopyCandidate candidate)
    {
        var metadata = candidate.Metadata;
        var parent = string.Join(
            "/",
            candidate.ParentWalk.Select(item =>
                $"{item.ControlType}|{item.AutomationId}|{item.ClassName}|{item.FrameworkId}"));
        var runtimeIdentity = candidate.ParentWalk.Count == 0
            ? GetRuntimeIdentity(candidate.Element)
            : string.Empty;
        return string.Join(
            "|",
            metadata.ControlType,
            metadata.AutomationId,
            metadata.ClassName,
            metadata.FrameworkId,
            metadata.Name,
            FormatBounds(metadata.Bounds),
            parent,
            runtimeIdentity);
    }

    private static string GetRuntimeIdentity(AutomationElement element)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            return runtimeId is { Length: > 0 }
                ? string.Join(",", runtimeId)
                : string.Empty;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return string.Empty;
        }
    }

    private static void WriteCopyCandidateDiagnostics(
        Guid runId,
        int candidateIndex,
        CodexRunCopyCandidate candidate,
        ISet<string> emittedKeys)
    {
        var key = BuildCandidateKey(candidate);
        if (!emittedKeys.Add(key))
        {
            return;
        }

        var runLabel = runId == Guid.Empty ? "baseline" : runId.ToString();
        var metadata = candidate.Metadata;
        StartupDiagnostics.Write(
            $"codex_run_copy_candidate runId={runLabel} candidateIndex={candidateIndex} " +
            $"controlType={Sanitize(metadata.ControlType)} " +
            $"localizedControlType={Sanitize(metadata.LocalizedControlType)} " +
            $"automationId={Sanitize(metadata.AutomationId)} " +
            $"className={Sanitize(metadata.ClassName)} " +
            $"frameworkId={Sanitize(metadata.FrameworkId)} " +
            $"enabled={metadata.IsEnabled} offscreen={metadata.IsOffscreen} " +
            $"invokePattern={metadata.InvokePatternAvailable} " +
            $"bounds={FormatBounds(metadata.Bounds)} " +
            $"name={Sanitize(metadata.Name)}");
        foreach (var parent in candidate.ParentWalk)
        {
            StartupDiagnostics.Write(
                $"codex_run_copy_parent runId={runLabel} candidateIndex={candidateIndex} " +
                $"depth={parent.Depth} controlType={Sanitize(parent.ControlType)} " +
                $"automationId={Sanitize(parent.AutomationId)} " +
                $"className={Sanitize(parent.ClassName)} " +
                $"frameworkId={Sanitize(parent.FrameworkId)} " +
                $"bounds={FormatBounds(parent.Bounds)} " +
                $"childCount={parent.ChildCount} siblingCount={parent.SiblingCount}");
        }
    }

    private static bool IsDescendantOf(AutomationElement element, AutomationElement ancestor)
    {
        var current = element;
        for (var depth = 0; depth <= MaxCopyAncestorDepth && current is not null; depth++)
        {
            if (IsSameElement(current, ancestor))
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static bool IsSameElement(AutomationElement first, AutomationElement second)
    {
        try
        {
            return ReferenceEquals(first, second) || first.Equals(second);
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return ReferenceEquals(first, second);
        }
    }

    private static string GetControlTypeName(ControlType controlType)
    {
        var name = controlType.ProgrammaticName;
        var separator = name.LastIndexOf('.');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private static bool IsChromium(string? frameworkId) =>
        string.Equals(frameworkId, "Chrome", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frameworkId, "Chromium", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frameworkId, "WebView2", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsAny(string? value, IEnumerable<string> tokens) =>
        tokens.Any(token => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);

    private static string FormatBounds(UiAutomationBounds bounds) =>
        $"{bounds.Left:0.##},{bounds.Top:0.##},{bounds.Width:0.##},{bounds.Height:0.##}";

    private static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Replace(' ', '_').Replace('\t', '_').Replace('\r', '_').Replace('\n', '_');

    private static string HashFingerprint(string value)
    {
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static bool IsUiAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or
        InvalidOperationException or
        COMException or
        ExternalException;

    private sealed record AssistantActionInspection(
        bool HasCopyButton,
        int ActionButtonCount,
        bool HasFeedbackAction,
        string ActionRowStructuralFingerprint);

    private sealed class AutomationElementReferenceComparer : IEqualityComparer<AutomationElement>
    {
        public static AutomationElementReferenceComparer Instance { get; } = new();

        public bool Equals(AutomationElement? x, AutomationElement? y) =>
            ReferenceEquals(x, y);

        public int GetHashCode(AutomationElement obj) =>
            RuntimeHelpers.GetHashCode(obj);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
    }
}
