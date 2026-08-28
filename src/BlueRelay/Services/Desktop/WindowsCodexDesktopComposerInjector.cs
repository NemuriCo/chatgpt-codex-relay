using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using BlueRelay.Diagnostics;
using WpfDataFormats = System.Windows.DataFormats;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace BlueRelay.Services.Desktop;

public sealed class WindowsCodexDesktopComposerInjector : ICodexDesktopComposerInjector
{
    private const int MaxShortlistedWindows = 8;
    private const int MaxComposerDepth = 48;
    private const int MaxSiblingNodes = 256;
    private static readonly ComposerTraversalLimits ControlViewLimits =
        new(MaxNodes: 3000, MaxDepth: MaxComposerDepth, MaxSiblings: MaxSiblingNodes);
    private static readonly ComposerTraversalLimits RawViewLimits =
        new(MaxNodes: 3000, MaxDepth: MaxComposerDepth, MaxSiblings: MaxSiblingNodes);
    private static readonly TimeSpan WindowDiscoveryBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ComposerProbeBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ComposerWriteVerificationTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ComposerWriteVerificationPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PasteAcceptanceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PasteAcceptancePollInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan ForegroundActivationTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ForegroundActivationPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ClipboardRetryBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClipboardRetryInterval = TimeSpan.FromMilliseconds(25);
    private const int ReferenceScanMaxAncestorDepth = 4;
    private const int ReferenceScanMaxDepth = 6;
    private const int ReferenceScanMaxNodes = 512;

    private readonly CodexComposerOperationCoordinator _operationCoordinator;
    private readonly ICodexKeyboardInputSender _keyboardInputSender;

    public WindowsCodexDesktopComposerInjector(
        TimeSpan? fillTimeout = null,
        ICodexKeyboardInputSender? keyboardInputSender = null)
    {
        _operationCoordinator = new CodexComposerOperationCoordinator(fillTimeout);
        _keyboardInputSender = keyboardInputSender ?? new NativeKeyboardInputSender();
    }

    public async Task<OpenAiDesktopInspection> InspectOpenAiDesktopWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var workerTask = StaAutomationWorker.RunAsync(() => CaptureInspectionOnWorker(stopwatch, cancellationToken));
        var timeoutTask = Task.Delay(InspectionTimeout);
        var cancellationTask = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan);
        var completedTask = await Task.WhenAny(workerTask, timeoutTask, cancellationTask).ConfigureAwait(false);

        if (completedTask == workerTask)
        {
            try
            {
                var capture = await workerTask.ConfigureAwait(false);
                return capture.Inspection;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                StartupDiagnostics.WriteException("Codex composer inspection", exception);
                return EmptyInspection();
            }
        }

        StartupDiagnostics.Write($"Codex composer inspection timed out elapsedMs={stopwatch.ElapsedMilliseconds}");
        _ = ObserveWorkerAsync(workerTask, "Codex composer inspection worker");
        if (completedTask == cancellationTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return EmptyInspection();
    }

    public Task<CodexComposerInjectionResult> InjectAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool allowReplacingExistingText = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(CodexComposerInjectionResult.Failed(
                "codex_task_empty",
                "The task text is empty."));
        }

        var stopwatch = Stopwatch.StartNew();
        return _operationCoordinator.RunAsync(
            operationToken =>
            {
                CodexComposerDiagnostics.WriteStage("worker_started", stopwatch);
                return InjectOnWorker(
                    text,
                    operationToken,
                    stopwatch,
                    allowReplacingExistingText,
                    _keyboardInputSender);
            },
            cancellationToken);
    }

    private static CodexComposerInjectionResult InjectOnWorker(
        string text,
        CancellationToken cancellationToken,
        Stopwatch stopwatch,
        bool allowReplacingExistingText,
        ICodexKeyboardInputSender keyboardInputSender)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<NativeWindowCandidate> windows;
        CodexComposerDiagnostics.WriteStage("window_scan_started", stopwatch);
        try
        {
            windows = DiscoverOpenAiWindows(cancellationToken, stopwatch);
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("window_scan_completed", stopwatch);
        }
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<AutomationCandidate> candidates;
        CodexComposerDiagnostics.WriteStage("composer_search_started", stopwatch);
        try
        {
            candidates = ProbeComposerCandidates(windows, cancellationToken, stopwatch);
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("composer_search_completed", stopwatch);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!CodexComposerCandidateSelector.TrySelect(
                candidates.Select(candidate => candidate.Candidate).ToList(),
                out var selected) || selected is null)
        {
            StartupDiagnostics.Write("Codex composer result=composer_not_found");
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "No unambiguous editable Codex composer was found.");
        }

        var selectedWindow = windows.FirstOrDefault(window => window.Handle == selected.Metadata.Handle);
        CodexComposerDiagnostics.WriteStage("window_ownership_confirmed", stopwatch);
        StartupDiagnostics.Write(
            $"Codex composer ownership hwnd={selected.Metadata.Handle.ToInt64()} " +
            $"owner_pid={selectedWindow?.ProcessId.ToString() ?? "unknown"} " +
            $"renderer_pid={selected.Metadata.ProcessId}");
        CodexComposerDiagnostics.WriteStage("window_selected", stopwatch);
        StartupDiagnostics.Write(
            $"Codex composer candidate controlType={selected.Metadata.ControlType} " +
            $"framework={selected.Metadata.FrameworkId} className={selected.Metadata.ClassName} " +
            $"valuePattern={selected.SupportsValuePattern} " +
            $"valuePatternReadOnly={selected.IsValueReadOnly} " +
            $"textPattern={selected.SupportsTextPattern}");
        CodexComposerDiagnostics.WriteStage("composer_candidate", stopwatch);
        CodexComposerDiagnostics.WriteStage("composer_selected", stopwatch);
        if (!CodexComposerCandidateResolver.TryResolveElement(
                candidates.Select(candidate => (candidate.Element, candidate.Candidate)),
                selected,
                out AutomationElement? target) || target is null)
        {
            CodexComposerDiagnostics.WriteStage("composer_selected_element_missing", stopwatch);
            StartupDiagnostics.Write("Codex composer composer_selected_element_missing");
            return CodexComposerInjectionResult.Failed(
                "codex_composer_selection_lost",
                "The selected Codex composer could not be resolved for injection.");
        }

        StartupDiagnostics.Write("Codex composer result=composer_selected");
        var targetProcessId = selectedWindow?.ProcessId ?? selected.Metadata.ProcessId;
        WritePayloadDiagnostics(text);
        cancellationToken.ThrowIfCancellationRequested();
        var focusReady = false;
        var foregroundReady = false;
        var uiaFocus = false;
        CodexComposerDiagnostics.WriteStage("focus_started", stopwatch);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foregroundReady = TryActivateCodexWindow(
                selected.Metadata.Handle,
                "composer_focus",
                cancellationToken,
                out _);
            if (foregroundReady)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.SetFocus();
                cancellationToken.ThrowIfCancellationRequested();
                uiaFocus = HasFocus(target);
            }

            var foregroundAfterFocus = NativeMethods.GetForegroundWindow();
            var foregroundMatches = foregroundAfterFocus == selected.Metadata.Handle;
            StartupDiagnostics.Write(
                $"Codex composer focus_result uiaFocus={uiaFocus} " +
                $"foreground={foregroundMatches}");
            focusReady = foregroundReady && uiaFocus && foregroundMatches;
        }
        catch (ElementNotAvailableException)
        {
            focusReady = false;
        }
        catch (COMException)
        {
            focusReady = false;
        }
        catch (InvalidOperationException)
        {
            focusReady = false;
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("focus_completed", stopwatch);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!foregroundReady)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_foreground_failed",
                "Codex could not be made the foreground window.");
        }

        if (!focusReady)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "The Codex composer could not receive focus.");
        }

        CodexComposerDiagnostics.WriteStage("focus_success", stopwatch);

        if (!allowReplacingExistingText)
        {
            var contentState = ReadExistingContent(target, selected);
            cancellationToken.ThrowIfCancellationRequested();
            if (CodexComposerContentGuard.RequiresConfirmation(contentState, allowReplacingExistingText: false))
            {
                CodexComposerDiagnostics.WriteStage(
                    contentState == CodexComposerContentState.HasContent
                        ? "existing_content_detected"
                        : "existing_content_unknown",
                    stopwatch);
                return CodexComposerInjectionResult.Failed(
                    contentState == CodexComposerContentState.HasContent
                        ? "codex_composer_existing_text"
                        : "codex_composer_content_unknown",
                    contentState == CodexComposerContentState.HasContent
                        ? "The Codex composer already contains text."
                        : "The Codex composer content could not be inspected safely.");
            }
        }

        CodexComposerDiagnostics.WriteStage("write_started", stopwatch);
        try
        {
            if (CodexComposerCandidateSelector.RequiresClipboardPaste(selected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartupDiagnostics.Write("Codex composer write_method=clipboard_prosemirror");
                StartupDiagnostics.Write("Codex composer clipboard_fallback_started");
                var pasteResult = PasteWithClipboardFallback(
                    target,
                    selected.Metadata.Handle,
                    text,
                    cancellationToken,
                    selectAllBeforePaste: allowReplacingExistingText,
                    keyboardInputSender);
                if (pasteResult.Success)
                {
                    CodexComposerDiagnostics.WriteStage("fill_verified", stopwatch);
                }

                return AttachFillTarget(
                    pasteResult,
                    selected.Metadata.Handle,
                    targetProcessId,
                    selected.Metadata);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (TrySetValue(target, text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartupDiagnostics.Write("Codex composer write_method=value_pattern");
                var verification = VerifyComposerWrite(target, text, cancellationToken);
                WriteVerificationDiagnostics("value_pattern_write", verification);
                if (verification.IsVerified)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.SetFocus();
                    cancellationToken.ThrowIfCancellationRequested();
                    CodexComposerDiagnostics.WriteStage("fill_verified", stopwatch);
                    return AttachFillTarget(
                        new CodexComposerInjectionResult(
                        true,
                        "codex_composer_value_verified",
                        "Codex composer filled and verified.",
                        Mode: CodexComposerInjectionMode.ValuePatternVerified),
                        selected.Metadata.Handle,
                        targetProcessId,
                        selected.Metadata);
                }

                StartupDiagnostics.Write("Codex composer value_pattern_verification_failed");
                cancellationToken.ThrowIfCancellationRequested();
                if (!PrepareForClipboardFallback(target, keyboardInputSender, cancellationToken))
                {
                    return CodexComposerInjectionResult.Failed(
                        "codex_composer_verification_failed",
                        "The Codex composer did not contain the complete task text and could not be safely cleared.");
                }

                StartupDiagnostics.Write("Codex composer clipboard_fallback_started");
                return AttachFillTarget(
                    PasteWithClipboardFallback(
                        target,
                        selected.Metadata.Handle,
                        text,
                        cancellationToken,
                        selectAllBeforePaste: false,
                        keyboardInputSender),
                    selected.Metadata.Handle,
                    targetProcessId,
                    selected.Metadata);
            }

            StartupDiagnostics.Write("Codex composer write_method=clipboard");
            StartupDiagnostics.Write("Codex composer clipboard_fallback_started");
            cancellationToken.ThrowIfCancellationRequested();
            var fallbackResult = PasteWithClipboardFallback(
                target,
                selected.Metadata.Handle,
                text,
                cancellationToken,
                selectAllBeforePaste: allowReplacingExistingText,
                keyboardInputSender);
            if (fallbackResult.Success)
            {
                CodexComposerDiagnostics.WriteStage("fill_verified", stopwatch);
            }

            return AttachFillTarget(
                fallbackResult,
                selected.Metadata.Handle,
                targetProcessId,
                selected.Metadata);
        }
        catch (ElementNotAvailableException exception)
        {
            StartupDiagnostics.Write($"Codex composer became unavailable: {exception.Message}");
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "The Codex composer is no longer available.");
        }
        catch (COMException exception)
        {
            StartupDiagnostics.WriteException("Codex composer injection", exception);
            return CodexComposerInjectionResult.Failed(
                "codex_composer_injection_failed",
                "Codex composer injection failed.");
        }
        catch (InvalidOperationException exception)
        {
            StartupDiagnostics.WriteException("Codex composer injection", exception);
            return CodexComposerInjectionResult.Failed(
                "codex_composer_injection_failed",
                "Codex composer injection failed.");
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("write_completed", stopwatch);
        }
    }

    private static InspectionCapture CaptureInspectionOnWorker(
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        CodexComposerDiagnostics.WriteStage("inspection_window_scan_started", stopwatch);
        IReadOnlyList<NativeWindowCandidate> windows;
        try
        {
            windows = DiscoverOpenAiWindows(cancellationToken, stopwatch);
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("inspection_window_scan_completed", stopwatch);
        }

        var metadataWindows = new List<UiAutomationMetadata>();
        var candidates = new List<AutomationCandidate>();
        CodexComposerDiagnostics.WriteStage("inspection_composer_search_started", stopwatch);
        try
        {
            foreach (var windowCandidate in windows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetWindowElement(windowCandidate, out var window, out var metadata))
                {
                    continue;
                }

                metadataWindows.Add(metadata);
                var search = SearchComposerWindow(
                    window,
                    metadata,
                    cancellationToken,
                    stopwatch);
                candidates.AddRange(search.Candidates);
            }
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("inspection_composer_search_completed", stopwatch);
        }

        var inspection = new OpenAiDesktopInspection(
            metadataWindows,
            candidates.Select(candidate => candidate.Candidate).ToList());
        WriteInspectionDiagnostics(inspection);
        return new InspectionCapture(inspection, candidates);
    }

    private static IReadOnlyList<NativeWindowCandidate> DiscoverOpenAiWindows(
        CancellationToken cancellationToken,
        Stopwatch stopwatch)
    {
        var nativeWindows = new List<NativeWindowCandidate>();
        var currentProcessId = Environment.ProcessId;
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (stopwatch.Elapsed > WindowDiscoveryBudget || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle) || !NativeMethods.IsWindowEnabled(handle))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == currentProcessId)
            {
                return true;
            }

            var title = NativeMethods.GetWindowText(handle);
            var className = NativeMethods.GetClassName(handle);
            var bounds = NativeMethods.GetWindowBounds(handle);
            nativeWindows.Add(new NativeWindowCandidate(
                handle,
                (int)processId,
                title,
                className,
                null,
                bounds,
                handle == NativeMethods.GetForegroundWindow()));
            return nativeWindows.Count < 64;
        }, IntPtr.Zero);

        var scoredWindows = new List<NativeWindowCandidate>();
        foreach (var nativeWindow in nativeWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processName = TryGetProcessName(nativeWindow.ProcessId);
            var processPath = TryGetProcessPath(nativeWindow.ProcessId);
            if (!OpenAiWindowClassifier.IsLikelyOpenAiWindow(
                    nativeWindow.WindowTitle,
                    nativeWindow.ClassName,
                    processName,
                    processPath))
            {
                continue;
            }

            var score = OpenAiWindowClassifier.Score(
                nativeWindow.WindowTitle,
                nativeWindow.ClassName,
                processName,
                processPath,
                nativeWindow.IsForeground);
            scoredWindows.Add(nativeWindow with
            {
                ProcessName = processName,
                ProcessPath = processPath,
                Score = score
            });
        }

        return scoredWindows
            .OrderByDescending(window => window.Score)
            .ThenByDescending(window => window.IsForeground)
            .Take(MaxShortlistedWindows)
            .ToList();
    }

    private static IReadOnlyList<AutomationCandidate> ProbeComposerCandidates(
        IReadOnlyList<NativeWindowCandidate> windows,
        CancellationToken cancellationToken,
        Stopwatch operationStopwatch)
    {
        var candidates = new List<AutomationCandidate>();
        var probeStopwatch = Stopwatch.StartNew();
        StartupDiagnostics.Write($"Codex composer window_count={windows.Count}");
        foreach (var windowCandidate in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (probeStopwatch.Elapsed > ComposerProbeBudget)
            {
                break;
            }

            if (!TryGetWindowElement(windowCandidate, out var window, out var metadata))
            {
                continue;
            }

            StartupDiagnostics.Write(
                $"Codex composer selected_window hwnd={windowCandidate.Handle.ToInt64()} " +
                $"pid={windowCandidate.ProcessId} " +
                $"title={SanitizeDiagnosticValue(windowCandidate.WindowTitle)} " +
                $"class={SanitizeDiagnosticValue(windowCandidate.ClassName)} " +
                $"process={SanitizeDiagnosticValue(windowCandidate.ProcessName)}");
            var search = SearchComposerWindow(
                window,
                metadata,
                cancellationToken,
                probeStopwatch);
            candidates.AddRange(search.Candidates);
        }

        return candidates;
    }

    private static ComposerWindowSearchResult SearchComposerWindow(
        AutomationElement window,
        UiAutomationMetadata windowMetadata,
        CancellationToken cancellationToken,
        Stopwatch budgetStopwatch)
    {
        var controlView = ProbeComposerPhase(
            window,
            windowMetadata,
            TreeWalker.ControlViewWalker,
            ControlViewLimits,
            allowNonEditFallback: false,
            cancellationToken,
            budgetStopwatch);
        WriteTraversalDiagnostics("control_view", controlView);

        ComposerPhaseResult? rawViewFallback = null;
        var candidates = controlView.Candidates.ToList();
        if (!controlView.FoundHighConfidenceCandidate &&
            budgetStopwatch.Elapsed <= ComposerProbeBudget)
        {
            var rawView = ProbeComposerPhase(
                window,
                windowMetadata,
                TreeWalker.RawViewWalker,
                RawViewLimits,
                allowNonEditFallback: true,
                cancellationToken,
                budgetStopwatch);
            rawViewFallback = rawView;
            WriteTraversalDiagnostics("raw_view_fallback", rawView);
            candidates.AddRange(rawView.Candidates);
        }

        return new ComposerWindowSearchResult(
            DeduplicateCandidates(candidates),
            controlView,
            rawViewFallback);
    }

    private static ComposerPhaseResult ProbeComposerPhase(
        AutomationElement window,
        UiAutomationMetadata windowMetadata,
        TreeWalker walker,
        ComposerTraversalLimits limits,
        bool allowNonEditFallback,
        CancellationToken cancellationToken,
        Stopwatch budgetStopwatch)
    {
        var phaseStopwatch = Stopwatch.StartNew();
        try
        {
            var roots = GetChildren(window, walker);
            var result = BoundedComposerTreeTraversal.Search<AutomationElement, AutomationCandidate>(
                roots,
                limits,
                budgetStopwatch,
                ComposerProbeBudget,
                element => GetChildren(element, walker),
                IsEditControl,
                allowNonEditFallback ? IsFallbackControl : static _ => false,
                HasProseMirrorClass,
                element => TryCreateAutomationCandidate(
                    element,
                    window,
                    windowMetadata.Handle,
                    NativeMethods.GetForegroundWindow()),
                candidate => CodexComposerCandidateSelector.IsHighConfidence(candidate.Candidate),
                cancellationToken);
            return new ComposerPhaseResult(
                result.Candidates,
                result.Statistics,
                result.FoundHighConfidenceCandidate,
                phaseStopwatch.ElapsedMilliseconds);
        }
        catch (ElementNotAvailableException)
        {
            return ComposerPhaseResult.Empty(phaseStopwatch.ElapsedMilliseconds);
        }
        catch (COMException)
        {
            return ComposerPhaseResult.Empty(phaseStopwatch.ElapsedMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return ComposerPhaseResult.Empty(phaseStopwatch.ElapsedMilliseconds);
        }
    }

    private static IReadOnlyList<AutomationElement> GetChildren(
        AutomationElement element,
        TreeWalker walker)
    {
        var children = new List<AutomationElement>();
        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            return children;
        }
        catch (COMException)
        {
            return children;
        }
        catch (InvalidOperationException)
        {
            return children;
        }

        while (child is not null && children.Count < MaxSiblingNodes)
        {
            children.Add(child);
            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
            catch (COMException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        return children;
    }

    private static IReadOnlyList<AutomationCandidate> DeduplicateCandidates(
        IEnumerable<AutomationCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => (
                candidate.Candidate.Metadata.Handle,
                candidate.Candidate.Metadata.ControlType,
                candidate.Candidate.Metadata.AutomationId,
                candidate.Candidate.Metadata.ClassName),
                EqualityComparer<(IntPtr, string, string, string)>.Default)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Candidate.SemanticScore)
                .First())
            .ToList();
    }

    private static AutomationCandidate? TryCreateAutomationCandidate(
        AutomationElement element,
        AutomationElement window,
        IntPtr fallbackWindowHandle,
        IntPtr foregroundHandle)
    {
        if (!TryReadMetadata(
                element,
                window,
                fallbackWindowHandle,
                foregroundHandle,
                out var metadata))
        {
            return null;
        }

        metadata = metadata with { IsLikelyOpenAiWindow = true };
        var patternInfo = ReadEditablePatterns(element);
        if (!patternInfo.SupportsValuePattern && !patternInfo.SupportsTextPattern)
        {
            return null;
        }

        var candidate = new CodexComposerCandidate(
            metadata,
            true,
            patternInfo.SupportsValuePattern,
            patternInfo.IsValueReadOnly,
            0,
            patternInfo.SupportsTextPattern);
        return new AutomationCandidate(
            element,
            candidate with { SemanticScore = CodexComposerCandidateSelector.Score(candidate) });
    }

    private static bool IsEditControl(AutomationElement element)
    {
        return HasControlType(element, ControlType.Edit);
    }

    private static bool IsFallbackControl(AutomationElement element)
    {
        return HasControlType(element, ControlType.Document) ||
               HasControlType(element, ControlType.Custom);
    }

    private static bool HasControlType(AutomationElement element, ControlType controlType)
    {
        try
        {
            return element.Current.ControlType == controlType;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasProseMirrorClass(AutomationElement element)
    {
        try
        {
            return CodexComposerCandidateSelector.HasClassToken(
                element.Current.ClassName ?? string.Empty,
                "ProseMirror");
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WriteTraversalDiagnostics(string phase, ComposerPhaseResult result)
    {
        var statistics = result.Statistics;
        StartupDiagnostics.Write(
            $"Codex composer {phase} visited_nodes={statistics.VisitedNodes} " +
            $"max_depth_reached={statistics.MaxDepthReached} " +
            $"edit_controls_seen={statistics.EditControlsSeen} " +
            $"prosemirror_seen={statistics.ProseMirrorSeen} " +
            $"elapsed_ms={result.ElapsedMilliseconds}");
    }

    private static bool TryGetWindowElement(
        NativeWindowCandidate windowCandidate,
        out AutomationElement window,
        out UiAutomationMetadata metadata)
    {
        window = null!;
        metadata = default!;
        try
        {
            window = AutomationElement.FromHandle(windowCandidate.Handle);
            if (!TryReadMetadata(
                    window,
                    window,
                    windowCandidate.Handle,
                    NativeMethods.GetForegroundWindow(),
                    out metadata))
            {
                return false;
            }

            metadata = metadata with
            {
                IsLikelyOpenAiWindow = true,
                WindowTitle = string.IsNullOrWhiteSpace(metadata.WindowTitle)
                    ? windowCandidate.WindowTitle
                    : metadata.WindowTitle
            };
            return IsVisibleWindow(metadata);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadMetadata(
        AutomationElement element,
        AutomationElement window,
        IntPtr fallbackWindowHandle,
        IntPtr foregroundHandle,
        out UiAutomationMetadata metadata)
    {
        metadata = default!;
        try
        {
            var current = element.Current;
            var windowCurrent = window.Current;
            var handle = new IntPtr(windowCurrent.NativeWindowHandle);
            if (handle == IntPtr.Zero)
            {
                handle = fallbackWindowHandle;
            }

            metadata = new UiAutomationMetadata(
                handle,
                current.ProcessId,
                windowCurrent.Name ?? string.Empty,
                GetControlTypeName(current.ControlType),
                current.AutomationId ?? string.Empty,
                current.Name ?? string.Empty,
                current.ClassName ?? string.Empty,
                current.FrameworkId ?? string.Empty,
                current.IsEnabled,
                current.IsKeyboardFocusable,
                current.IsOffscreen,
                ToBounds(current.BoundingRectangle),
                ToBounds(windowCurrent.BoundingRectangle),
                BuildParentHierarchy(element, window),
                false,
                handle != IntPtr.Zero && handle == foregroundHandle);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static (bool SupportsValuePattern, bool IsValueReadOnly, bool SupportsTextPattern) ReadEditablePatterns(
        AutomationElement element)
    {
        var supportsValuePattern = false;
        var isValueReadOnly = false;
        var supportsTextPattern = false;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                supportsValuePattern = true;
                isValueReadOnly = ((ValuePattern)pattern).Current.IsReadOnly;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }

        try
        {
            supportsTextPattern = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }

        return (supportsValuePattern, isValueReadOnly, supportsTextPattern);
    }

    private static bool TrySetValue(AutomationElement target, string text)
    {
        try
        {
            if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                return false;
            }

            var valuePattern = (ValuePattern)pattern;
            if (valuePattern.Current.IsReadOnly)
            {
                return false;
            }

            valuePattern.SetValue(text);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WritePayloadDiagnostics(string text)
    {
        CodexComposerDiagnostics.WritePayloadMetadata("composer_payload", text);
    }

    private static CodexComposerWriteVerification VerifyComposerWrite(
        AutomationElement target,
        string source,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var verification = ReadComposerAccessibility(target, source);
        while (!verification.IsVerified && stopwatch.Elapsed < ComposerWriteVerificationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(ComposerWriteVerificationPollInterval);
            verification = ReadComposerAccessibility(target, source);
        }

        return verification;
    }

    private static void WriteVerificationDiagnostics(
        string stage,
        CodexComposerWriteVerification verification)
    {
        StartupDiagnostics.Write(
            $"Codex composer {stage} " +
            $"destinationValueLength={verification.ValueLength} " +
            $"destinationTextLength={verification.TextLength} " +
            $"valueAvailable={verification.ValueAvailable} " +
            $"textAvailable={verification.TextAvailable} " +
            $"valueMatchesSource={verification.ValueMatchesSource} " +
            $"textMatchesSource={verification.TextMatchesSource} " +
            $"semanticAnchorCount={verification.SemanticAnchorCount} " +
            $"semanticAnchorMatchedCount={verification.SemanticAnchorMatchedCount} " +
            $"semanticAnchorsInOrder={verification.SemanticAnchorsInOrder} " +
            $"verificationMode={GetVerificationMode(verification)}");
    }

    private static string GetVerificationMode(CodexComposerWriteVerification verification) =>
        verification.IsVerified
            ? "inline_exact"
            : verification.IsRichTextTransformedAccepted
                ? "rich_text_transformed"
                : "failed";

    private static void WriteAttachmentSnapshotDiagnostics(
        string phase,
        CodexComposerReferenceSnapshot snapshot,
        CodexComposerReferenceSnapshot? before = null)
    {
        var newAttachmentDetected = before is not null &&
                                     snapshot.HasNewAttachmentsSince(before);
        StartupDiagnostics.Write(
            $"Codex composer attachment_snapshot_{phase} " +
            $"available={snapshot.IsAvailable} " +
            $"count={snapshot.AttachmentCount} " +
            $"newAttachmentDetected={newAttachmentDetected}");
    }

    private static bool PrepareForClipboardFallback(
        AutomationElement target,
        ICodexKeyboardInputSender keyboardInputSender,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryClearPartialContent(target, cancellationToken))
        {
            return true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.SetFocus();
            cancellationToken.ThrowIfCancellationRequested();
            var sendResult = keyboardInputSender.SendCtrlA();
            WriteKeyboardInputDiagnostics("select_all", sendResult);
            return sendResult.Succeeded;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryClearPartialContent(
        AutomationElement target,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                return false;
            }

            var valuePattern = (ValuePattern)pattern;
            if (valuePattern.Current.IsReadOnly)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            valuePattern.SetValue(string.Empty);
            cancellationToken.ThrowIfCancellationRequested();
            var verification = VerifyComposerWrite(target, string.Empty, cancellationToken);
            WriteVerificationDiagnostics("partial_content_clear", verification);
            return CodexComposerWriteVerifier.IsEmpty(verification);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static CodexComposerWriteVerification ReadComposerAccessibility(
        AutomationElement target,
        string source)
    {
        var valuePatternAvailable = false;
        var textPatternAvailable = false;
        string? value = null;
        string? text = null;

        try
        {
            if (target.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                text = ((TextPattern)pattern).DocumentRange.GetText(-1);
                textPatternAvailable = true;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                value = ((ValuePattern)pattern).Current.Value;
                valuePatternAvailable = true;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return CodexComposerWriteVerifier.Verify(
            source,
            valuePatternAvailable,
            value,
            textPatternAvailable,
            text);
    }

    private static CodexComposerContentState ReadExistingContent(
        AutomationElement target,
        CodexComposerCandidate candidate)
    {
        var valuePatternAvailable = false;
        var textPatternAvailable = false;
        string? value = null;
        string? text = null;

        try
        {
            if (target.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                var textPattern = (TextPattern)pattern;
                text = textPattern.DocumentRange.GetText(-1);
                textPatternAvailable = true;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                value = ((ValuePattern)pattern).Current.Value;
                valuePatternAvailable = true;
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        var isProseMirror = CodexComposerCandidateSelector.HasClassToken(
            candidate.Metadata.ClassName,
            "ProseMirror");
        var contentState = CodexComposerContentGuard.DetermineContentState(
            isProseMirror,
            textPatternAvailable,
            text,
            valuePatternAvailable,
            value,
            candidate.Metadata.Name);
        var normalizedValue = CodexComposerContentGuard.NormalizeComposerTextForEmptiness(value);
        var normalizedName = CodexComposerContentGuard.NormalizeComposerTextForEmptiness(candidate.Metadata.Name);
        var normalizedText = CodexComposerContentGuard.NormalizeComposerTextForEmptiness(text);
        var valueEqualsName = valuePatternAvailable &&
                              normalizedValue.Equals(normalizedName, StringComparison.Ordinal);
        var textEqualsName = textPatternAvailable &&
                             normalizedText.Equals(normalizedName, StringComparison.Ordinal);
        StartupDiagnostics.Write(
            $"Codex composer composer_content_probe " +
            $"valuePatternAvailable={valuePatternAvailable} " +
            $"valueLength={value?.Length ?? 0} " +
            $"textPatternAvailable={textPatternAvailable} " +
            $"textLength={text?.Length ?? 0} " +
            $"nameLength={candidate.Metadata.Name.Length} " +
            $"valueEqualsName={valueEqualsName} " +
            $"textEqualsName={textEqualsName} " +
            $"className={SanitizeDiagnosticValue(candidate.Metadata.ClassName)} " +
            $"finalContentState={contentState}");
        return contentState;
    }

    private static CodexComposerInjectionResult PasteWithClipboardFallback(
        AutomationElement target,
        IntPtr windowHandle,
        string text,
        CancellationToken cancellationToken,
        bool selectAllBeforePaste,
        ICodexKeyboardInputSender keyboardInputSender)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var foregroundReady = TryActivateCodexWindow(
                windowHandle,
                "paste",
                cancellationToken,
                out _);
            if (!foregroundReady)
            {
                return CodexComposerInjectionResult.Failed(
                    "codex_foreground_failed",
                    "Codex could not be made the foreground window for paste.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            target.SetFocus();
            cancellationToken.ThrowIfCancellationRequested();
            var uiaFocus = HasFocus(target);
            var foregroundMatches = NativeMethods.GetForegroundWindow() == windowHandle;
            StartupDiagnostics.Write(
                $"Codex composer paste_focus uiaFocus={uiaFocus} " +
                $"foreground={foregroundMatches}");
            if (!uiaFocus)
            {
                return CodexComposerInjectionResult.Failed(
                    "codex_composer_not_found",
                    "The Codex composer could not receive focus for clipboard paste.");
            }

            if (!foregroundMatches)
            {
                return CodexComposerInjectionResult.Failed(
                    "codex_foreground_failed",
                    "Codex lost foreground focus before paste.");
            }

            if (selectAllBeforePaste)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selectAllResult = keyboardInputSender.SendCtrlA();
                WriteKeyboardInputDiagnostics("select_all", selectAllResult);
                if (!selectAllResult.Succeeded)
                {
                    return CodexComposerInjectionResult.Failed(
                        "codex_composer_verification_failed",
                        "The partial Codex composer content could not be safely selected for replacement.");
                }
            }

            var referencesBeforePaste = CapturePastedTextReferenceSnapshot(target);
            StartupDiagnostics.Write(
                $"Codex composer reference_snapshot_before available={referencesBeforePaste.IsAvailable} " +
                $"count={referencesBeforePaste.Count}");
            WriteAttachmentSnapshotDiagnostics("before", referencesBeforePaste);

            var existingTextReadable = TryReadExistingClipboardText(
                "composer",
                out var existingClipboardTextAvailable,
                out var existingClipboardText);
            var existingClipboardTextMatchesPayload =
                existingTextReadable &&
                existingClipboardTextAvailable &&
                CodexClipboardTextComparer.Matches(existingClipboardText, text);
            StartupDiagnostics.Write(
                $"Codex composer clipboard_existing_text_available={existingClipboardTextAvailable} " +
                $"clipboard_existing_text_matches_payload={existingClipboardTextMatchesPayload}");

            var clipboardWriteSkippedExistingPayload = existingClipboardTextMatchesPayload;
            var snapshotCaptured = false;
            var snapshotMode = clipboardWriteSkippedExistingPayload
                ? CodexClipboardSnapshotMode.NotNeeded
                : CodexClipboardSnapshotMode.Unavailable;
            ClipboardSnapshot snapshot = null!;
            if (!clipboardWriteSkippedExistingPayload)
            {
                snapshotCaptured = ClipboardSnapshot.TryCapture(
                    out snapshot,
                    out var snapshotFormat,
                    out var snapshotFailure);
                snapshotMode = snapshotCaptured
                    ? CodexClipboardSnapshotMode.Full
                    : CodexClipboardSnapshotMode.Unavailable;
                StartupDiagnostics.Write(
                    $"Codex composer clipboard_snapshot_mode={FormatSnapshotMode(snapshotMode)} " +
                    $"format={SanitizeDiagnosticValue(snapshotFormat)} " +
                    $"failure={SanitizeDiagnosticValue(snapshotFailure)}");
            }

            var result = CodexComposerInjectionResult.Failed(
                "codex_composer_injection_failed",
                "The paste operation could not be completed.");
            try
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var clipboardSet = clipboardWriteSkippedExistingPayload ||
                                       TrySetClipboardText(text, "composer", cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var clipboardVerified = clipboardWriteSkippedExistingPayload ||
                                            TryVerifyClipboardText(
                                                text,
                                                "composer",
                                                out _,
                                                out _);
                    StartupDiagnostics.Write(
                        $"Codex composer clipboard_write_skipped_existing_payload={clipboardWriteSkippedExistingPayload}");
                    var foregroundBeforeSend = NativeMethods.GetForegroundWindow() == windowHandle;
                    var uiaFocusBeforeSend = HasFocus(target);
                    StartupDiagnostics.Write(
                        $"Codex composer paste_preflight " +
                        $"uiaFocus={uiaFocusBeforeSend} " +
                        $"foreground={foregroundBeforeSend} " +
                        $"clipboardSet={clipboardSet} " +
                        $"clipboardVerified={clipboardVerified}");
                    if (!CodexComposerInputDecision.CanSendFormalCtrlV(
                            foregroundBeforeSend,
                            uiaFocusBeforeSend,
                            clipboardSet,
                            clipboardVerified,
                            clipboardSnapshotAvailable: true))
                    {
                        result = !clipboardVerified
                            ? CodexComposerInjectionResult.Failed(
                                "codex_clipboard_write_failed",
                                "The task text could not be verified in the clipboard.")
                            : !foregroundBeforeSend
                                ? CodexComposerInjectionResult.Failed(
                                    "codex_foreground_failed",
                                    "Codex lost foreground focus before paste.")
                                : CodexComposerInjectionResult.Failed(
                                    "codex_composer_not_found",
                                    "The Codex composer lost UI Automation focus before paste.");
                    }
                    else
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sendResult = keyboardInputSender.SendCtrlV();
                        WriteKeyboardInputDiagnostics("send_paste", sendResult);
                        if (!sendResult.Succeeded)
                        {
                            result = CodexComposerInjectionResult.Failed(
                                "codex_composer_injection_failed",
                                "The Ctrl+V input could not be sent to the Codex composer.");
                        }
                        else
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var acceptance = WaitForPasteAcceptance(
                                target,
                                text,
                                referencesBeforePaste,
                                cancellationToken);
                            WriteVerificationDiagnostics("clipboard_paste_result", acceptance.Verification);
                            StartupDiagnostics.Write(
                                $"Codex composer reference_snapshot_after available={acceptance.ReferencesAfterPaste.IsAvailable} " +
                                $"count={acceptance.ReferencesAfterPaste.Count} " +
                                $"new={acceptance.ReferencesAfterPaste.HasNewReferencesSince(referencesBeforePaste)}");
                            WriteAttachmentSnapshotDiagnostics(
                                "after",
                                acceptance.ReferencesAfterPaste,
                                referencesBeforePaste);
                            StartupDiagnostics.Write(
                                $"Codex composer attachment_detection " +
                                $"matchedBy={acceptance.ReferencesAfterPaste.GetNewDetectionKindSince(referencesBeforePaste)}");

                            result = acceptance.Mode switch
                            {
                                CodexComposerInjectionMode.ClipboardInlineVerified =>
                                    new CodexComposerInjectionResult(
                                        true,
                                        "codex_composer_clipboard_inline_verified",
                                        "Codex composer clipboard paste was verified.",
                                        UsedClipboardFallback: true,
                                        Mode: CodexComposerInjectionMode.ClipboardInlineVerified),
                                CodexComposerInjectionMode.ClipboardInlineTransformedAccepted =>
                                    new CodexComposerInjectionResult(
                                        true,
                                        "codex_composer_clipboard_inline_transformed",
                                        "Codex composer clipboard paste was verified.",
                                        UsedClipboardFallback: true,
                                        Mode: CodexComposerInjectionMode.ClipboardInlineTransformedAccepted),
                                CodexComposerInjectionMode.ClipboardReferenceAccepted =>
                                    new CodexComposerInjectionResult(
                                        true,
                                        "clipboard_paste_accepted_as_reference",
                                        "Codex accepted the long text as a pasted-text reference.",
                                        UsedClipboardFallback: true,
                                        Mode: CodexComposerInjectionMode.ClipboardReferenceAccepted),
                                _ => CodexComposerInjectionResult.Failed(
                                    "codex_composer_verification_failed",
                                    "The Codex composer did not contain the complete task text after paste.")
                            };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    StartupDiagnostics.WriteException("Paste into Codex composer", exception);
                    result = CodexComposerInjectionResult.Failed(
                        "codex_composer_injection_failed",
                        "The paste operation could not be completed.");
                }
            }
            finally
            {
                var restoreAttempted = snapshotCaptured && !clipboardWriteSkippedExistingPayload;
                var restoreUnavailable = !snapshotCaptured && !clipboardWriteSkippedExistingPayload;
                var restoreSuccess = !restoreUnavailable;
                if (restoreAttempted)
                {
                    restoreSuccess = snapshot.TryRestore();
                }

                var clipboardWarning = restoreUnavailable || !restoreSuccess;
                StartupDiagnostics.Write(
                    $"Codex composer clipboard_snapshot_mode={FormatSnapshotMode(snapshotMode)} " +
                    $"clipboard_restore_attempted={restoreAttempted} " +
                    $"clipboard_restore_success={restoreSuccess} " +
                    $"fill_success={result.Success} " +
                    $"clipboard_warning={clipboardWarning}");
                result = result with
                {
                    UsedClipboardFallback = true,
                    ClipboardRestoreFailed = restoreAttempted && !restoreSuccess,
                    ClipboardSnapshotMode = snapshotMode,
                    ClipboardRestoreUnavailable = restoreUnavailable
                };
            }

            return result;
        }
        catch (ElementNotAvailableException)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "The Codex composer is no longer available.");
        }
        catch (COMException)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "The Codex composer could not receive focus for clipboard paste.");
        }
        catch (InvalidOperationException)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
            "The Codex composer could not receive focus for clipboard paste.");
        }
    }

    private static bool TryActivateCodexWindow(
        IntPtr codexWindowHandle,
        string stage,
        CancellationToken cancellationToken,
        out ForegroundActivation activation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foregroundBefore = NativeMethods.GetForegroundWindow();
        var blueRelayWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
        cancellationToken.ThrowIfCancellationRequested();
        var setForegroundSucceeded = NativeMethods.SetForegroundWindow(codexWindowHandle);
        var foregroundAfter = foregroundBefore;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ForegroundActivationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foregroundAfter = NativeMethods.GetForegroundWindow();
            if (foregroundAfter == codexWindowHandle)
            {
                break;
            }

            Thread.Sleep(ForegroundActivationPollInterval);
        }

        var foregroundMatches = foregroundAfter == codexWindowHandle;
        activation = new ForegroundActivation(
            blueRelayWindowHandle,
            codexWindowHandle,
            foregroundBefore,
            setForegroundSucceeded,
            foregroundAfter,
            foregroundMatches);
        StartupDiagnostics.Write(
            $"Codex composer {stage}_foreground " +
            $"blueRelayHwnd={blueRelayWindowHandle.ToInt64()} " +
            $"codexHwnd={codexWindowHandle.ToInt64()} " +
            $"foreground_before={foregroundBefore.ToInt64()} " +
            $"set_foreground_result={setForegroundSucceeded} " +
            $"foreground_after={foregroundAfter.ToInt64()} " +
            $"foreground_matches_target={foregroundMatches}");
        return setForegroundSucceeded && foregroundMatches;
    }

    private static CodexComposerInjectionResult AttachFillTarget(
        CodexComposerInjectionResult result,
        IntPtr targetWindowHandle,
        int targetProcessId,
        UiAutomationMetadata composerMetadata)
    {
        if (!result.Success)
        {
            return result;
        }

        var target = new CodexComposerFillTarget(
            targetWindowHandle,
            targetProcessId,
            result.Mode,
            DateTimeOffset.UtcNow,
            composerMetadata.Bounds,
            composerMetadata.AutomationId,
            composerMetadata.ClassName,
            composerMetadata.ParentHierarchy);
        StartupDiagnostics.Write(
            $"codex_fill_target captured hwnd={targetWindowHandle.ToInt64()} " +
            $"pid={targetProcessId} verificationMode={result.Mode}");
        return result with { FillTarget = target };
    }

    private static bool TrySetClipboardText(
        string text,
        string stage,
        CancellationToken cancellationToken)
    {
        var success = TryClipboardOperation(
            $"{stage}.SetText",
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                System.Windows.Clipboard.SetText(text, WpfTextDataFormat.UnicodeText);
                return true;
            },
            out _);
        StartupDiagnostics.Write(
            $"Codex composer {stage}_clipboard_set " +
            $"success={success} sourceLength={text.Length}");
        return success;
    }

    private static bool TryReadExistingClipboardText(
        string stage,
        out bool textAvailable,
        out string? text)
    {
        var localTextAvailable = false;
        string? localText = null;
        var operationSucceeded = TryClipboardOperation(
            $"{stage}.ExistingText",
            () =>
            {
                if (!System.Windows.Clipboard.ContainsText())
                {
                    return true;
                }

                localText = System.Windows.Clipboard.GetText(WpfTextDataFormat.UnicodeText);
                localTextAvailable = true;
                return true;
            },
            out _);
        textAvailable = operationSucceeded && localTextAvailable;
        text = localText;
        StartupDiagnostics.Write(
            $"Codex composer {stage}_clipboard_existing_text " +
            $"operationSucceeded={operationSucceeded} " +
            $"available={textAvailable} " +
            $"length={text?.Length ?? 0}");
        return operationSucceeded;
    }

    private static bool TryVerifyClipboardText(
        string text,
        string stage,
        out bool containsText,
        out int clipboardLength)
    {
        var localContainsText = false;
        var localClipboardLength = 0;
        var success = TryClipboardOperation(
            $"{stage}.Verify",
            () =>
            {
                localContainsText = System.Windows.Clipboard.ContainsText();
                if (!localContainsText)
                {
                    localClipboardLength = 0;
                    return false;
                }

                var clipboardText = System.Windows.Clipboard.GetText(WpfTextDataFormat.UnicodeText);
                localClipboardLength = clipboardText.Length;
                return CodexClipboardWriteVerifier.Verify(
                    localContainsText,
                    localClipboardLength,
                    text.Length).IsVerified;
            },
            out _);
        containsText = localContainsText;
        clipboardLength = localClipboardLength;
        StartupDiagnostics.Write(
            $"Codex composer {stage}_clipboard_verify " +
            $"success={success} " +
            $"containsText={containsText} " +
            $"clipboardLength={clipboardLength} " +
            $"sourceLength={text.Length}");
        return success;
    }

    private static string FormatSnapshotMode(CodexClipboardSnapshotMode mode) =>
        mode switch
        {
            CodexClipboardSnapshotMode.Full => "full",
            CodexClipboardSnapshotMode.Unavailable => "unavailable",
            CodexClipboardSnapshotMode.NotNeeded => "not_needed",
            _ => "unavailable"
        };

    private static bool TryClipboardOperation(
        string operation,
        Func<bool> action,
        out Exception? lastException)
    {
        lastException = null;
        StartupDiagnostics.Write(
            $"clipboard_worker operation={SanitizeDiagnosticValue(operation)} " +
            $"threadId={Environment.CurrentManagedThreadId} " +
            $"ApartmentState={Thread.CurrentThread.GetApartmentState()}");
        var stopwatch = Stopwatch.StartNew();
        var maximumAttempts = CodexClipboardRetryPolicy.GetMaximumAttempts(
            ClipboardRetryBudget,
            ClipboardRetryInterval);

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                if (action())
                {
                    return true;
                }

                return false;
            }
            catch (Exception exception) when (IsClipboardException(exception))
            {
                lastException = exception;
                WriteClipboardException(operation, exception);
                var remaining = ClipboardRetryBudget - stopwatch.Elapsed;
                if (attempt >= maximumAttempts || remaining <= TimeSpan.Zero)
                {
                    StartupDiagnostics.Write(
                        $"clipboard_operation operation={SanitizeDiagnosticValue(operation)} " +
                        "result=clipboard_busy");
                    return false;
                }

                var delay = CodexClipboardRetryPolicy.GetDelay(
                    remaining,
                    ClipboardRetryInterval);
                StartupDiagnostics.Write(
                    $"clipboard_retry operation={SanitizeDiagnosticValue(operation)} " +
                    $"attempt={attempt} delayMs={(long)delay.TotalMilliseconds}");
                Thread.Sleep(delay);
            }
            catch (Exception exception)
            {
                lastException = exception;
                WriteClipboardException(operation, exception);
                return false;
            }
        }

        return false;
    }

    private static bool IsClipboardException(Exception exception) =>
        exception is COMException ||
        exception is ExternalException ||
        exception is ThreadStateException ||
        exception is InvalidOperationException;

    private static void WriteClipboardException(string stage, Exception exception)
    {
        StartupDiagnostics.Write(
            $"ERROR stage={SanitizeDiagnosticValue(stage)} " +
            $"type={exception.GetType().FullName} " +
            $"hresult=0x{exception.HResult:X8} " +
            $"message={SanitizeDiagnosticValue(exception.Message)}");
    }

    private static void WriteKeyboardInputDiagnostics(
        string stage,
        CodexKeyboardInputSendResult result)
    {
        StartupDiagnostics.Write(
            $"Codex composer {stage} " +
            $"requestedInputCount={result.RequestedInputCount} " +
            $"sentInputCount={result.SentInputCount} " +
            $"win32Error={result.Win32Error}");
    }

    private static int ReadComposerLength(AutomationElement target)
    {
        var verification = ReadComposerAccessibility(target, string.Empty);
        return Math.Max(verification.ValueLength, verification.TextLength);
    }

    private static PasteAcceptanceObservation WaitForPasteAcceptance(
        AutomationElement target,
        string source,
        CodexComposerReferenceSnapshot referencesBeforePaste,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var verification = ReadComposerAccessibility(target, source);
        var referencesAfterPaste = CapturePastedTextReferenceSnapshot(target);
        while (true)
        {
            if (verification.IsVerified)
            {
                StartupDiagnostics.Write("Codex composer clipboard_paste_result mode=inline_exact");
                return new PasteAcceptanceObservation(
                    CodexComposerInjectionMode.ClipboardInlineVerified,
                    verification,
                    referencesAfterPaste);
            }

            if (verification.IsRichTextTransformedAccepted)
            {
                StartupDiagnostics.Write("Codex composer clipboard_paste_result mode=inline_transformed");
                return new PasteAcceptanceObservation(
                    CodexComposerInjectionMode.ClipboardInlineTransformedAccepted,
                    verification,
                    referencesAfterPaste);
            }

            if (referencesAfterPaste.HasNewReferencesSince(referencesBeforePaste))
            {
                StartupDiagnostics.Write("Codex composer clipboard_paste_result mode=reference");
                return new PasteAcceptanceObservation(
                    CodexComposerInjectionMode.ClipboardReferenceAccepted,
                    verification,
                    referencesAfterPaste);
            }

            if (stopwatch.Elapsed >= PasteAcceptanceTimeout)
            {
                StartupDiagnostics.Write("Codex composer clipboard_paste_result mode=failed");
                return new PasteAcceptanceObservation(
                    CodexComposerInjectionMode.VerificationFailed,
                    verification,
                    referencesAfterPaste);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(PasteAcceptancePollInterval);
            cancellationToken.ThrowIfCancellationRequested();
            verification = ReadComposerAccessibility(target, source);
            referencesAfterPaste = CapturePastedTextReferenceSnapshot(target);
        }
    }

    private static CodexComposerReferenceSnapshot CapturePastedTextReferenceSnapshot(
        AutomationElement composer)
    {
        try
        {
            var referenceIds = new HashSet<string>(StringComparer.Ordinal);
            var attachmentDetectionKinds = new Dictionary<string, string>(StringComparer.Ordinal);
            var visitedNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var roots = new List<AutomationElement> { composer };
            var composerBounds = ToBounds(composer.Current.BoundingRectangle);
            var ancestor = composer;
            for (var depth = 0; depth < ReferenceScanMaxAncestorDepth; depth++)
            {
                ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
                if (ancestor is null)
                {
                    break;
                }

                roots.Add(ancestor);
            }

            var queue = new Queue<(AutomationElement Element, int Depth)>();
            foreach (var root in roots)
            {
                queue.Enqueue((root, 0));
            }

            var visitedNodes = 0;
            while (queue.Count > 0 && visitedNodes < ReferenceScanMaxNodes)
            {
                var (element, depth) = queue.Dequeue();
                var nodeId = GetAutomationNodeIdentity(element, visitedNodes);
                if (!visitedNodeIds.Add(nodeId))
                {
                    continue;
                }

                visitedNodes++;
                var children = depth < ReferenceScanMaxDepth
                    ? GetChildren(element, TreeWalker.RawViewWalker)
                    : [];
                var current = element.Current;
                var attachmentMetadata = new CodexComposerAttachmentNodeMetadata(
                    GetControlTypeName(current.ControlType),
                    current.AutomationId ?? string.Empty,
                    current.Name ?? string.Empty,
                    current.ClassName ?? string.Empty,
                    current.FrameworkId ?? string.Empty,
                    BuildParentHierarchy(element, composer),
                    ToBounds(current.BoundingRectangle),
                    composerBounds,
                    current.IsOffscreen,
                    IsWithinComposerReferenceScope(element, composer),
                    children.Count,
                    HasCurrentPattern(element, InvokePattern.Pattern),
                    current.LocalizedControlType ?? string.Empty,
                    current.HelpText ?? string.Empty,
                    current.ItemStatus ?? string.Empty,
                    current.ItemType ?? string.Empty);
                var attachmentDetectionKind =
                    CodexComposerAttachmentDetector.TryClassify(attachmentMetadata);
                if (attachmentDetectionKind is not null)
                {
                    attachmentDetectionKinds[CodexComposerAttachmentDetector.BuildFingerprint(attachmentMetadata)] =
                        attachmentDetectionKind;
                }

                if (TryGetReferenceNodeId(element, out var referenceId))
                {
                    referenceIds.Add(referenceId);
                }

                if (depth >= ReferenceScanMaxDepth)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }

            return new CodexComposerReferenceSnapshot(
                true,
                referenceIds,
                attachmentDetectionKinds);
        }
        catch (ElementNotAvailableException)
        {
            return new CodexComposerReferenceSnapshot(false, new HashSet<string>(StringComparer.Ordinal));
        }
        catch (COMException)
        {
            return new CodexComposerReferenceSnapshot(false, new HashSet<string>(StringComparer.Ordinal));
        }
        catch (InvalidOperationException)
        {
            return new CodexComposerReferenceSnapshot(false, new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private static bool TryGetReferenceNodeId(
        AutomationElement element,
        out string referenceId)
    {
        referenceId = string.Empty;
        try
        {
            var current = element.Current;
            if (!CodexComposerWriteVerifier.HasReferencedPastedTextSignal(
                    current.Name,
                    current.AutomationId,
                    current.ClassName,
                    current.HelpText,
                    current.ItemStatus,
                    current.ItemType,
                    current.LocalizedControlType))
            {
                return false;
            }

            referenceId = GetAutomationNodeIdentity(element, 0);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsWithinComposerReferenceScope(
        AutomationElement element,
        AutomationElement composer)
    {
        try
        {
            var composerContainers = new List<AutomationElement>();
            var composerAncestor = composer;
            for (var depth = 0; depth < ReferenceScanMaxAncestorDepth; depth++)
            {
                composerContainers.Add(composerAncestor);
                composerAncestor = TreeWalker.RawViewWalker.GetParent(composerAncestor);
                if (composerAncestor is null)
                {
                    break;
                }
            }

            if (element.Equals(composer))
            {
                return true;
            }

            var current = element;
            for (var depth = 0; depth < ReferenceScanMaxDepth; depth++)
            {
                current = TreeWalker.RawViewWalker.GetParent(current);
                if (current is null)
                {
                    return false;
                }

                if (current.Equals(composer) ||
                    composerContainers.Any(container => current.Equals(container)))
                {
                    return true;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    private static bool HasCurrentPattern(
        AutomationElement element,
        AutomationPattern pattern)
    {
        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetAutomationNodeIdentity(
        AutomationElement element,
        int fallbackIndex)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            if (runtimeId.Length > 0)
            {
                return $"runtime:{string.Join(",", runtimeId)}";
            }

            var current = element.Current;
            var bounds = current.BoundingRectangle;
            return $"metadata:{current.AutomationId}|{current.ClassName}|" +
                   $"{current.ControlType?.ProgrammaticName}|" +
                   $"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}|" +
                   $"{fallbackIndex}";
        }
        catch (ElementNotAvailableException)
        {
            return $"unavailable:{fallbackIndex}";
        }
        catch (COMException)
        {
            return $"com:{fallbackIndex}";
        }
        catch (InvalidOperationException)
        {
            return $"invalid:{fallbackIndex}";
        }
    }

    private static bool HasFocus(AutomationElement target)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return false;
            }

            if (focused.Equals(target))
            {
                return true;
            }

            var current = focused;
            for (var depth = 0; depth < MaxComposerDepth && current is not null; depth++)
            {
                current = TreeWalker.RawViewWalker.GetParent(current);
                if (current is not null && current.Equals(target))
                {
                    return true;
                }
            }

            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsVisibleWindow(UiAutomationMetadata metadata)
    {
        return metadata.IsEnabled && !metadata.IsOffscreen && !metadata.Bounds.IsEmpty;
    }

    private static UiAutomationBounds ToBounds(Rect bounds) =>
        new(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

    private static string GetControlTypeName(ControlType? controlType)
    {
        if (controlType == ControlType.Edit)
        {
            return "Edit";
        }

        if (controlType == ControlType.Document)
        {
            return "Document";
        }

        if (controlType == ControlType.Custom)
        {
            return "Custom";
        }

        if (controlType == ControlType.Pane)
        {
            return "Pane";
        }

        return controlType?.ProgrammaticName ?? string.Empty;
    }

    private static string BuildParentHierarchy(AutomationElement element, AutomationElement window)
    {
        var hierarchy = new List<string>();
        try
        {
            var current = TreeWalker.RawViewWalker.GetParent(element);
            var depth = 0;
            while (current is not null && !current.Equals(window) && depth++ < MaxComposerDepth)
            {
                var details = current.Current;
                var controlType = GetControlTypeName(details.ControlType);
                var automationId = details.AutomationId ?? string.Empty;
                var node = string.IsNullOrWhiteSpace(automationId)
                    ? controlType
                    : $"{controlType}#{automationId}";
                var className = details.ClassName ?? string.Empty;
                hierarchy.Add(string.IsNullOrWhiteSpace(className)
                    ? node
                    : $"{node}@{className}");
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }

        hierarchy.Reverse();
        return string.Join(" > ", hierarchy);
    }

    private static string? TryGetProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SanitizeDiagnosticValue(string value)
    {
        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static async Task ObserveWorkerAsync(Task workerTask, string operationName)
    {
        try
        {
            await workerTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.WriteException(operationName, exception);
        }
    }

    private static OpenAiDesktopInspection EmptyInspection() =>
        new([], []);

    private static void WriteInspectionDiagnostics(OpenAiDesktopInspection inspection)
    {
        var candidateWindows = inspection.Windows
            .Where(window => window.IsLikelyOpenAiWindow)
            .ToList();
        StartupDiagnostics.Write($"Codex composer inspection windows={candidateWindows.Count} controls={inspection.ComposerCandidates.Count}");
        foreach (var window in candidateWindows)
        {
            StartupDiagnostics.Write(
                $"Codex window hwnd={window.Handle.ToInt64()} pid={window.ProcessId} " +
                $"controlType={window.ControlType} automationId={window.AutomationId} " +
                $"enabled={window.IsEnabled} keyboardFocusable={window.IsKeyboardFocusable}");
        }

        foreach (var candidate in inspection.ComposerCandidates.Where(candidate => candidate.IsOpenAiWindow))
        {
            StartupDiagnostics.Write(
                $"Codex composer candidate hwnd={candidate.Metadata.Handle.ToInt64()} pid={candidate.Metadata.ProcessId} " +
                $"controlType={candidate.Metadata.ControlType} automationId={candidate.Metadata.AutomationId} " +
                $"framework={candidate.Metadata.FrameworkId} className={candidate.Metadata.ClassName} " +
                $"enabled={candidate.Metadata.IsEnabled} keyboardFocusable={candidate.Metadata.IsKeyboardFocusable} " +
                $"valuePattern={candidate.SupportsValuePattern} " +
                $"valuePatternReadOnly={candidate.IsValueReadOnly} " +
                $"textPattern={candidate.SupportsTextPattern}");
        }
    }

    private sealed record AutomationCandidate(
        AutomationElement Element,
        CodexComposerCandidate Candidate);

    private sealed record InspectionCapture(
        OpenAiDesktopInspection Inspection,
        IReadOnlyList<AutomationCandidate> Candidates);

    private sealed record PasteAcceptanceObservation(
        CodexComposerInjectionMode Mode,
        CodexComposerWriteVerification Verification,
        CodexComposerReferenceSnapshot ReferencesAfterPaste);

    private sealed record ComposerPhaseResult(
        IReadOnlyList<AutomationCandidate> Candidates,
        ComposerTraversalStatistics Statistics,
        bool FoundHighConfidenceCandidate,
        long ElapsedMilliseconds)
    {
        public static ComposerPhaseResult Empty(long elapsedMilliseconds) =>
            new(
                [],
                new ComposerTraversalStatistics(0, 0, 0, 0, false, false, false),
                false,
                elapsedMilliseconds);
    }

    private sealed record ComposerWindowSearchResult(
        IReadOnlyList<AutomationCandidate> Candidates,
        ComposerPhaseResult ControlView,
        ComposerPhaseResult? RawViewFallback);

    private sealed record NativeWindowCandidate(
        IntPtr Handle,
        int ProcessId,
        string WindowTitle,
        string ClassName,
        string? ProcessPath,
        UiAutomationBounds Bounds,
        bool IsForeground,
        int Score = 0,
        string ProcessName = "");

    private sealed record ForegroundActivation(
        IntPtr BlueRelayWindowHandle,
        IntPtr TargetWindowHandle,
        IntPtr ForegroundBefore,
        bool SetForegroundSucceeded,
        IntPtr ForegroundAfter,
        bool ForegroundMatchesTarget);

    private sealed class ClipboardSnapshot
    {
        private readonly string? _text;
        private readonly bool _hadText;

        private ClipboardSnapshot(string? text, bool hadText)
        {
            _text = text;
            _hadText = hadText;
        }

        public static bool TryCapture(out ClipboardSnapshot snapshot) =>
            TryCapture(out snapshot, out _, out _);

        public static bool TryCapture(
            out ClipboardSnapshot snapshot,
            out string format,
            out string failureReason)
        {
            snapshot = null!;
            var localFormat = "unknown";
            var localFailureReason = string.Empty;
            ClipboardSnapshot? captured = null;
            var success = TryClipboardOperation(
                "snapshot.capture",
                () =>
                {
                    var data = System.Windows.Clipboard.GetDataObject();
                    var formats = data?.GetFormats(false) ?? [];
                    if (formats.Any(formatName => !IsPlainTextFormat(formatName)))
                    {
                        localFormat = "unsupported";
                        localFailureReason = "unsupported_format";
                        return false;
                    }

                    var hadText = data?.GetDataPresent(WpfDataFormats.UnicodeText) == true ||
                                  data?.GetDataPresent(WpfDataFormats.Text) == true;
                    var text = hadText
                        ? System.Windows.Clipboard.GetText(WpfTextDataFormat.UnicodeText)
                        : null;
                    captured = new ClipboardSnapshot(text, hadText);
                    localFormat = hadText ? "plain_text" : "empty";
                    return true;
                },
                out var lastException);

            format = localFormat;
            failureReason = localFailureReason;

            if (success && captured is not null)
            {
                snapshot = captured;
                StartupDiagnostics.Write(
                    $"clipboard_snapshot success=true format={SanitizeDiagnosticValue(format)}");
                return true;
            }

            if (lastException is not null && string.IsNullOrWhiteSpace(failureReason))
            {
                failureReason = "clipboard_exception";
            }

            if (string.IsNullOrWhiteSpace(failureReason))
            {
                failureReason = "unsupported_or_empty_result";
            }

            StartupDiagnostics.Write(
                $"clipboard_snapshot success=false " +
                $"format={SanitizeDiagnosticValue(format)} " +
                $"failure={SanitizeDiagnosticValue(failureReason)}");
            return false;
        }

        public bool TryRestore()
        {
            return TryClipboardOperation(
                "snapshot.restore",
                () =>
                {
                    System.Windows.Clipboard.Clear();
                    if (_hadText)
                    {
                        System.Windows.Clipboard.SetText(
                            _text ?? string.Empty,
                            WpfTextDataFormat.UnicodeText);
                    }

                    return true;
                },
                out _);
        }

        private static bool IsPlainTextFormat(string format)
        {
            return string.Equals(format, WpfDataFormats.Text, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(format, WpfDataFormats.UnicodeText, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(format, WpfDataFormats.StringFormat, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(format, WpfDataFormats.Locale, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static class OpenAiWindowClassifier
    {
        public static bool IsLikelyOpenAiWindow(
            UiAutomationMetadata metadata,
            string? processPath) =>
            IsLikelyOpenAiWindow(metadata.WindowTitle, metadata.ClassName, string.Empty, processPath);

        public static bool IsLikelyOpenAiWindow(
            string windowTitle,
            string className,
            string processName,
            string? processPath)
        {
            var signals = $"{windowTitle} {className} {processName} {processPath}";
            return signals.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
                   signals.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
                   signals.Contains("codex", StringComparison.OrdinalIgnoreCase);
        }

        public static int Score(
            string windowTitle,
            string className,
            string processName,
            string? processPath,
            bool isForeground)
        {
            var score = isForeground ? 100 : 0;
            var signals = $"{windowTitle} {className} {processName} {processPath}";
            if (signals.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            if (signals.Contains("openai", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            if (signals.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                score += 5;
            }

            return score;
        }
    }

    private sealed class NativeKeyboardInputSender : ICodexKeyboardInputSender
    {
        public CodexKeyboardInputSendResult SendCtrlV() => NativeMethods.SendPaste();

        public CodexKeyboardInputSendResult SendCtrlA() => NativeMethods.SendSelectAll();
    }

    private static class NativeMethods
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;

        public delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowEnabled(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr handle, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint numberOfInputs,
            CodexSendInputNative[] inputs,
            int sizeOfInput);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        public static string GetWindowText(IntPtr handle)
        {
            var text = new StringBuilder(512);
            GetWindowText(handle, text, text.Capacity);
            return text.ToString();
        }

        public static string GetClassName(IntPtr handle)
        {
            var className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            return className.ToString();
        }

        public static UiAutomationBounds GetWindowBounds(IntPtr handle)
        {
            return GetWindowRect(handle, out var rect)
                ? new UiAutomationBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
                : new UiAutomationBounds(0, 0, 0, 0);
        }

        public static CodexKeyboardInputSendResult SendPaste()
        {
            return SendKeyboardInput(CodexKeyboardInputSequence.CtrlV, "Ctrl+V");
        }

        public static CodexKeyboardInputSendResult SendSelectAll()
        {
            return SendKeyboardInput(CodexKeyboardInputSequence.CtrlA, "Ctrl+A");
        }

        private static CodexKeyboardInputSendResult SendKeyboardInput(
            IReadOnlyList<CodexKeyboardInput> sequence,
            string description)
        {
            var controlDownBeforeSend = WriteModifierDiagnostics(description);
            var inputs = sequence
                .Select(input => KeyInput(input.VirtualKey, input.KeyUp))
                .ToArray();
            var requested = inputs.Length;
            var inputSize = Marshal.SizeOf<CodexSendInputNative>();
            var keyboardInputSize = Marshal.SizeOf<CodexSendInputKeyboard>();
            StartupDiagnostics.Write(
                $"sendinput_environment " +
                $"process64Bit={Environment.Is64BitProcess} " +
                $"pointerSize={IntPtr.Size} " +
                $"inputSize={inputSize} " +
                $"keyboardInputSize={keyboardInputSize}");
            StartupDiagnostics.Write(
                $"sendinput_request count={requested} cbSize={inputSize}");
            uint sent = 0;
            var win32Error = 0;
            var sendInputException = false;
            try
            {
                sent = SendInput((uint)requested, inputs, inputSize);
                if (sent != requested)
                {
                    win32Error = Marshal.GetLastWin32Error();
                }
            }
            catch (Exception exception)
            {
                sendInputException = true;
                win32Error = Marshal.GetLastWin32Error();
                StartupDiagnostics.WriteException($"Send {description} input", exception);
            }

            StartupDiagnostics.Write(
                $"sendinput_result sent={sent} lastError={win32Error}");
            if (sent < requested && (sent > 0 || sendInputException || controlDownBeforeSend))
            {
                TryReleaseControlKey(description);
            }

            return new CodexKeyboardInputSendResult(
                requested,
                (int)sent,
                win32Error);
        }

        private static void TryReleaseControlKey(string description)
        {
            try
            {
                var release = new[]
                {
                    KeyInput(CodexKeyboardInputSequence.VirtualKeyControl, keyUp: true)
                };
                var inputSize = Marshal.SizeOf<CodexSendInputNative>();
                var sent = SendInput(1, release, inputSize);
                StartupDiagnostics.Write(
                    $"Codex composer {description} control_key_cleanup " +
                    $"requestedInputCount=1 sentInputCount={sent} " +
                    $"cbSize={inputSize} " +
                    $"win32Error={(sent == 1 ? 0 : Marshal.GetLastWin32Error())}");
            }
            catch (Exception exception)
            {
                StartupDiagnostics.WriteException($"Release Control after {description}", exception);
            }
        }

        private static bool WriteModifierDiagnostics(string description)
        {
            var controlDown = IsDown(GetAsyncKeyState(CodexKeyboardInputSequence.VirtualKeyControl));
            var shiftDown = IsDown(GetAsyncKeyState(0x10));
            var menuDown = IsDown(GetAsyncKeyState(0x12));
            StartupDiagnostics.Write(
                $"Codex composer {description} modifiers " +
                $"controlDown={controlDown} shiftDown={shiftDown} menuDown={menuDown}");
            return controlDown;
        }

        private static bool IsDown(short state) => (state & 0x8000) != 0;

        private static CodexSendInputNative KeyInput(ushort virtualKey, bool keyUp) =>
            new()
            {
                Type = InputKeyboard,
                Data = new CodexSendInputUnion
                {
                    Keyboard = new CodexSendInputKeyboard
                    {
                        VirtualKey = virtualKey,
                        Flags = keyUp ? KeyEventKeyUp : 0
                    }
                }
            };

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

    }
}
