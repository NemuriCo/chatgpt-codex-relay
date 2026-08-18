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

    private readonly CodexComposerOperationCoordinator _operationCoordinator;

    public WindowsCodexDesktopComposerInjector(TimeSpan? fillTimeout = null)
    {
        _operationCoordinator = new CodexComposerOperationCoordinator(fillTimeout);
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
            () =>
            {
                CodexComposerDiagnostics.WriteStage("worker_started", stopwatch);
                return InjectOnWorker(
                    text,
                    cancellationToken,
                    stopwatch,
                    allowReplacingExistingText);
            },
            cancellationToken);
    }

    private static CodexComposerInjectionResult InjectOnWorker(
        string text,
        CancellationToken cancellationToken,
        Stopwatch stopwatch,
        bool allowReplacingExistingText)
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
        var focusReady = false;
        CodexComposerDiagnostics.WriteStage("focus_started", stopwatch);
        try
        {
            NativeMethods.SetForegroundWindow(selected.Metadata.Handle);
            target.SetFocus();
            focusReady = HasFocus(target);
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

        if (!focusReady)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "The Codex composer could not receive focus.");
        }

        CodexComposerDiagnostics.WriteStage("focus_success", stopwatch);

        if (!allowReplacingExistingText)
        {
            var contentState = ReadExistingContent(target);
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
            if (TrySetValue(target, text))
            {
                StartupDiagnostics.Write("Codex composer write_method=value_pattern");
                target.SetFocus();
                CodexComposerDiagnostics.WriteStage("fill_success", stopwatch);
                return CodexComposerInjectionResult.Filled("Codex composer filled.");
            }

            StartupDiagnostics.Write("Codex composer write_method=clipboard");
            var fallbackResult = PasteWithClipboardFallback(target, text, cancellationToken);
            if (fallbackResult.Success)
            {
                CodexComposerDiagnostics.WriteStage("fill_success", stopwatch);
            }

            return fallbackResult;
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

    private static CodexComposerContentState ReadExistingContent(AutomationElement target)
    {
        try
        {
            if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                return CodexComposerContentState.Unknown;
            }

            var value = ((ValuePattern)pattern).Current.Value;
            return string.IsNullOrEmpty(value)
                ? CodexComposerContentState.Empty
                : CodexComposerContentState.HasContent;
        }
        catch (ElementNotAvailableException)
        {
            return CodexComposerContentState.Unknown;
        }
        catch (COMException)
        {
            return CodexComposerContentState.Unknown;
        }
        catch (InvalidOperationException)
        {
            return CodexComposerContentState.Unknown;
        }
    }

    private static CodexComposerInjectionResult PasteWithClipboardFallback(
        AutomationElement target,
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ClipboardSnapshot.TryCapture(out var snapshot))
        {
            return CodexComposerInjectionResult.Failed(
                "codex_clipboard_unsafe",
                "The clipboard could not be safely preserved for fallback paste.");
        }

        var result = CodexComposerInjectionResult.Failed(
            "codex_composer_injection_failed",
            "The paste operation could not be completed.");
        try
        {
            try
            {
                System.Windows.Clipboard.SetText(text, WpfTextDataFormat.UnicodeText);
                if (!NativeMethods.SendPaste())
                {
                    result = CodexComposerInjectionResult.Failed(
                        "codex_composer_injection_failed",
                        "The paste operation could not be sent to the Codex composer.");
                }
                else
                {
                    Thread.Sleep(100);
                    result = HasFocus(target)
                        ? CodexComposerInjectionResult.Filled("Codex composer filled with clipboard fallback.", usedClipboardFallback: true)
                        : CodexComposerInjectionResult.Failed(
                            "codex_composer_injection_failed",
                            "The Codex composer lost focus before paste completed.");
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
            var restoreFailed = !snapshot.TryRestore();
            if (restoreFailed)
            {
                StartupDiagnostics.Write("Codex composer clipboard restore failed; the original plain-text clipboard could not be restored.");
            }

            result = result with
            {
                UsedClipboardFallback = true,
                ClipboardRestoreFailed = restoreFailed
            };
        }

        return result;
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

    private sealed class ClipboardSnapshot
    {
        private readonly string? _text;
        private readonly bool _hadText;

        private ClipboardSnapshot(string? text, bool hadText)
        {
            _text = text;
            _hadText = hadText;
        }

        public static bool TryCapture(out ClipboardSnapshot snapshot)
        {
            snapshot = null!;
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                var formats = data?.GetFormats(false) ?? [];
                if (formats.Any(format => !IsPlainTextFormat(format)))
                {
                    return false;
                }

                var hadText = data?.GetDataPresent(WpfDataFormats.UnicodeText) == true ||
                              data?.GetDataPresent(WpfDataFormats.Text) == true;
                var text = hadText ? System.Windows.Clipboard.GetText(WpfTextDataFormat.UnicodeText) : null;
                snapshot = new ClipboardSnapshot(text, hadText);
                return true;
            }
            catch (Exception exception)
            {
                StartupDiagnostics.WriteException("Capture plain-text clipboard", exception);
                return false;
            }
        }

        public bool TryRestore()
        {
            try
            {
                System.Windows.Clipboard.Clear();
                if (_hadText)
                {
                    System.Windows.Clipboard.SetText(_text ?? string.Empty, WpfTextDataFormat.UnicodeText);
                }

                return true;
            }
            catch (Exception exception)
            {
                StartupDiagnostics.WriteException("Restore plain-text clipboard", exception);
                return false;
            }
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

    private static class NativeMethods
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;
        private const ushort VirtualKeyControl = 0x11;
        private const ushort VirtualKeyV = 0x56;

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
        private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInput);

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

        public static bool SendPaste()
        {
            var inputs = new[]
            {
                KeyInput(VirtualKeyControl, keyUp: false),
                KeyInput(VirtualKeyV, keyUp: false),
                KeyInput(VirtualKeyV, keyUp: true),
                KeyInput(VirtualKeyControl, keyUp: true)
            };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
        }

        private static INPUT KeyInput(ushort virtualKey, bool keyUp) =>
            new()
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
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

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }
    }
}
