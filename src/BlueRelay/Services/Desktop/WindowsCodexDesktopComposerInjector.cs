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
    private const int MaxComposerNodes = 512;
    private const int MaxComposerDepth = 12;
    private const int MaxSiblingNodes = 128;
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
        CancellationToken cancellationToken = default)
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
                return InjectOnWorker(text, cancellationToken, stopwatch);
            },
            cancellationToken);
    }

    private static CodexComposerInjectionResult InjectOnWorker(
        string text,
        CancellationToken cancellationToken,
        Stopwatch stopwatch)
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
                candidates.Select(candidate => candidate.Metadata).ToList(),
                out var selected) || selected is null)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_not_found",
                "No unambiguous editable Codex composer was found.");
        }

        CodexComposerDiagnostics.WriteStage("window_candidate_pid_match", stopwatch);
        StartupDiagnostics.Write(
            $"Codex composer window candidate matched pid={selected.Metadata.ProcessId} " +
            $"hwnd={selected.Metadata.Handle.ToInt64()}");
        CodexComposerDiagnostics.WriteStage("window_selected", stopwatch);
        StartupDiagnostics.Write(
            $"Codex composer candidate controlType={selected.Metadata.ControlType} " +
            $"framework={selected.Metadata.FrameworkId} className={selected.Metadata.ClassName} " +
            $"valuePattern={selected.SupportsValuePattern} " +
            $"valuePatternReadOnly={selected.IsValueReadOnly} " +
            $"textPattern={selected.SupportsTextPattern}");
        CodexComposerDiagnostics.WriteStage("composer_candidate", stopwatch);
        CodexComposerDiagnostics.WriteStage("composer_selected", stopwatch);
        var target = candidates.First(candidate => ReferenceEquals(candidate.Metadata, selected.Metadata)).Element;
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
                candidates.AddRange(ProbeComposerControls(
                    window,
                    metadata,
                    cancellationToken,
                    stopwatch,
                    MaxComposerNodes * 2));
            }
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("inspection_composer_search_completed", stopwatch);
        }

        var inspection = new OpenAiDesktopInspection(
            metadataWindows,
            candidates.Select(candidate => candidate.Metadata).ToList());
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
            var processPath = TryGetProcessPath(nativeWindow.ProcessId);
            if (!OpenAiWindowClassifier.IsLikelyOpenAiWindow(
                    nativeWindow.WindowTitle,
                    nativeWindow.ClassName,
                    processPath))
            {
                continue;
            }

            var score = OpenAiWindowClassifier.Score(
                nativeWindow.WindowTitle,
                nativeWindow.ClassName,
                processPath,
                nativeWindow.IsForeground);
            scoredWindows.Add(nativeWindow with { ProcessPath = processPath, Score = score });
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

            candidates.AddRange(ProbeComposerControls(
                window,
                metadata,
                cancellationToken,
                probeStopwatch,
                MaxComposerNodes));
        }

        return candidates;
    }

    private static IReadOnlyList<AutomationCandidate> ProbeComposerControls(
        AutomationElement window,
        UiAutomationMetadata windowMetadata,
        CancellationToken cancellationToken,
        Stopwatch budgetStopwatch,
        int maxNodes)
    {
        var candidates = new List<AutomationCandidate>();
        var walker = TreeWalker.RawViewWalker;
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        var visitedNodes = 0;

        try
        {
            var firstChild = walker.GetFirstChild(window);
            if (firstChild is not null)
            {
                pending.Push((firstChild, 1));
            }

            while (pending.Count > 0 &&
                   visitedNodes++ < maxNodes &&
                   budgetStopwatch.Elapsed <= ComposerProbeBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (element, depth) = pending.Pop();
                if (IsEditableControl(element))
                {
                    if (TryReadMetadata(
                            element,
                            window,
                            windowMetadata.Handle,
                            NativeMethods.GetForegroundWindow(),
                            out var metadata))
                    {
                        metadata = metadata with { IsLikelyOpenAiWindow = true };
                        var patternInfo = ReadEditablePatterns(element);
                        var candidate = new CodexComposerCandidate(
                            metadata,
                            true,
                            patternInfo.SupportsValuePattern,
                            patternInfo.IsValueReadOnly,
                            0,
                            patternInfo.SupportsTextPattern);
                        candidates.Add(new AutomationCandidate(
                            element,
                            candidate with { SemanticScore = CodexComposerCandidateSelector.Score(candidate) }));
                    }
                }

                if (depth >= MaxComposerDepth)
                {
                    continue;
                }

                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(element);
                var siblings = 0;
                while (child is not null && siblings++ < MaxSiblingNodes)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }

                for (var index = children.Count - 1; index >= 0; index--)
                {
                    pending.Push((children[index], depth + 1));
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

        return candidates;
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

    private static bool IsEditableControl(AutomationElement element)
    {
        try
        {
            var controlType = element.Current.ControlType;
            return controlType == ControlType.Edit ||
                   controlType == ControlType.Document ||
                   controlType == ControlType.Custom ||
                   controlType == ControlType.Group ||
                   controlType == ControlType.Pane;
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
        CodexComposerCandidate Metadata);

    private sealed record InspectionCapture(
        OpenAiDesktopInspection Inspection,
        IReadOnlyList<AutomationCandidate> Candidates);

    private sealed record NativeWindowCandidate(
        IntPtr Handle,
        int ProcessId,
        string WindowTitle,
        string ClassName,
        string? ProcessPath,
        UiAutomationBounds Bounds,
        bool IsForeground,
        int Score = 0);

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
            IsLikelyOpenAiWindow(metadata.WindowTitle, metadata.ClassName, processPath);

        public static bool IsLikelyOpenAiWindow(
            string windowTitle,
            string className,
            string? processPath)
        {
            var signals = $"{windowTitle} {className} {processPath}";
            return signals.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
                   signals.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
                   signals.Contains("codex", StringComparison.OrdinalIgnoreCase);
        }

        public static int Score(
            string windowTitle,
            string className,
            string? processPath,
            bool isForeground)
        {
            var score = isForeground ? 100 : 0;
            var signals = $"{windowTitle} {className} {processPath}";
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
