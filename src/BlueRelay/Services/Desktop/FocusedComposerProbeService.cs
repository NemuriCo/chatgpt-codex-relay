using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using BlueRelay.Diagnostics;

namespace BlueRelay.Services.Desktop;

public sealed class FocusedComposerProbeService
{
    public const int MaxParentDepth = 10;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private static readonly (string Name, AutomationPattern Pattern)[] Patterns =
    [
        ("ValuePattern", ValuePattern.Pattern),
        ("TextPattern", TextPattern.Pattern),
        ("TextPattern2", AutomationPattern.LookupById(10024)),
        ("LegacyIAccessiblePattern", AutomationPattern.LookupById(10018)),
        ("InvokePattern", InvokePattern.Pattern),
        ("ScrollPattern", ScrollPattern.Pattern),
        ("SelectionPattern", SelectionPattern.Pattern),
        ("SelectionItemPattern", SelectionItemPattern.Pattern),
        ("ExpandCollapsePattern", ExpandCollapsePattern.Pattern)
    ];

    private readonly TimeSpan _timeout;
    private readonly Func<Func<FocusedComposerProbeResult>, Task<FocusedComposerProbeResult>> _worker;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FocusedComposerProbeService(
        TimeSpan? timeout = null,
        Func<Func<FocusedComposerProbeResult>, Task<FocusedComposerProbeResult>>? worker = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _worker = worker ?? (operation => StaAutomationWorker.RunAsync(operation));
    }

    public async Task<FocusedComposerProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gateAcquired = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!gateAcquired)
        {
            return FocusedComposerProbeResult.Failed(
                "focused_probe_busy",
                "Another focused composer probe is already running.",
                TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        Task<FocusedComposerProbeResult>? workerTask = null;
        try
        {
            workerTask = _worker(() => ProbeOnSta(cancellationToken, stopwatch));
            var timeoutTask = Task.Delay(_timeout);
            var cancellationTask = cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan);
            var completedTask = await Task.WhenAny(workerTask, timeoutTask, cancellationTask).ConfigureAwait(false);

            if (completedTask == workerTask)
            {
                try
                {
                    return await workerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    StartupDiagnostics.WriteException("Focused composer probe worker", exception);
                    return FocusedComposerProbeResult.Failed(
                        "focused_probe_failed",
                        "Focused composer probe failed.",
                        stopwatch.Elapsed);
                }
                finally
                {
                    _gate.Release();
                }
            }

            _ = ReleaseWhenWorkerCompletesAsync(workerTask);
            if (completedTask == cancellationTask)
            {
                return FocusedComposerProbeResult.Failed(
                    "focused_probe_cancelled",
                    "Focused composer probe was cancelled.",
                    stopwatch.Elapsed);
            }

            StartupDiagnostics.Write($"Focused composer probe timed out elapsedMs={stopwatch.ElapsedMilliseconds}");
            return FocusedComposerProbeResult.Failed(
                "focused_probe_timeout",
                "Focused composer probe timed out.",
                stopwatch.Elapsed);
        }
        catch
        {
            if (gateAcquired && workerTask is null)
            {
                _gate.Release();
            }

            throw;
        }
    }

    private static FocusedComposerProbeResult ProbeOnSta(
        CancellationToken cancellationToken,
        Stopwatch stopwatch)
    {
        CodexComposerDiagnostics.WriteStage("focused_probe_worker_started", stopwatch);
        cancellationToken.ThrowIfCancellationRequested();

        AutomationElement focusedElement;
        CodexComposerDiagnostics.WriteStage("focused_element_read_started", stopwatch);
        try
        {
            focusedElement = AutomationElement.FocusedElement;
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("focused_element_read_completed", stopwatch);
        }

        if (focusedElement is null)
        {
            return FocusedComposerProbeResult.Failed(
                "focused_element_unavailable",
                "No focused UI Automation element was available.",
                stopwatch.Elapsed);
        }

        FocusedComposerElementMetadata focusedMetadata;
        try
        {
            focusedMetadata = ReadMetadata(focusedElement);
        }
        catch (Exception exception) when (IsUiAutomationException(exception))
        {
            return FocusedComposerProbeResult.Failed(
                "focused_element_unavailable",
                "The focused UI Automation element became unavailable.",
                stopwatch.Elapsed);
        }

        var parents = new List<FocusedComposerElementMetadata>();
        AutomationElement? topLevelElement = focusedElement;
        CodexComposerDiagnostics.WriteStage("focused_parent_walk_started", stopwatch);
        try
        {
            for (var level = 0; level < MaxParentDepth; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = TreeWalker.RawViewWalker.GetParent(topLevelElement);
                if (parent is null)
                {
                    break;
                }

                var parentMetadata = ReadMetadata(parent);
                parents.Add(parentMetadata);
                topLevelElement = parent;
            }
        }
        finally
        {
            CodexComposerDiagnostics.WriteStage("focused_parent_walk_completed", stopwatch);
        }

        CodexComposerDiagnostics.WriteStage("focused_window_pid_lookup_started", stopwatch);
        var windowMetadata = ReadWindowMetadata(focusedMetadata, cancellationToken);
        CodexComposerDiagnostics.WriteStage("focused_window_pid_lookup_completed", stopwatch);
        var isCodexDesktop = windowMetadata?.IsCodexDesktop == true;
        var message = isCodexDesktop
            ? "Focused element belongs to Codex Desktop."
            : "The current focus is not in Codex Desktop.";
        var code = isCodexDesktop ? "focused_codex_element" : "focused_not_codex";
        CodexComposerDiagnostics.WriteStage("focused_window_classification_completed", stopwatch);

        return new FocusedComposerProbeResult(
            isCodexDesktop,
            code,
            message,
            focusedMetadata,
            parents,
            windowMetadata,
            stopwatch.Elapsed);
    }

    private static FocusedComposerElementMetadata ReadMetadata(AutomationElement element)
    {
        var current = element.Current;
        var patternInfo = ReadSupportedPatterns(element);
        return new FocusedComposerElementMetadata(
            current.ProcessId,
            new IntPtr(current.NativeWindowHandle),
            GetControlTypeName(current.ControlType),
            current.LocalizedControlType ?? string.Empty,
            current.AutomationId ?? string.Empty,
            current.ClassName ?? string.Empty,
            current.Name ?? string.Empty,
            current.FrameworkId ?? string.Empty,
            current.IsEnabled,
            current.IsKeyboardFocusable,
            current.HasKeyboardFocus,
            current.IsOffscreen,
            ToBounds(current.BoundingRectangle),
            patternInfo.Patterns,
            patternInfo.ValuePatternIsReadOnly);
    }

    private static (IReadOnlyList<string> Patterns, bool? ValuePatternIsReadOnly) ReadSupportedPatterns(
        AutomationElement element)
    {
        var supported = new List<string>();
        bool? valuePatternIsReadOnly = null;
        foreach (var (name, pattern) in Patterns)
        {
            try
            {
                if (element.TryGetCurrentPattern(pattern, out var patternObject))
                {
                    supported.Add(name);
                    if (string.Equals(name, "ValuePattern", StringComparison.Ordinal))
                    {
                        valuePatternIsReadOnly = ((ValuePattern)patternObject).Current.IsReadOnly;
                    }
                }
            }
            catch (Exception exception) when (IsUiAutomationException(exception))
            {
                // A provider can reject one pattern while exposing the others.
            }
        }

        return (supported, valuePatternIsReadOnly);
    }

    private static FocusedComposerWindowMetadata? ReadWindowMetadata(
        FocusedComposerElementMetadata focused,
        CancellationToken cancellationToken)
    {
        if (!CodexDesktopWindowOwnership.TryResolveForProcess(
                focused.ProcessId,
                cancellationToken,
                out var window) ||
            window is null)
        {
            return null;
        }

        return new FocusedComposerWindowMetadata(
            window.ProcessId,
            window.Handle,
            window.WindowTitle,
            window.ClassName,
            window.ProcessName,
            true);
    }

    private static bool IsUiAutomationException(Exception exception) =>
        exception is ElementNotAvailableException ||
        exception is COMException ||
        exception is InvalidOperationException ||
        exception is ArgumentException;

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

        if (controlType == ControlType.Custom)
        {
            return "Custom";
        }

        if (controlType == ControlType.Group)
        {
            return "Group";
        }

        return controlType?.ProgrammaticName ?? string.Empty;
    }

    private async Task ReleaseWhenWorkerCompletesAsync(Task<FocusedComposerProbeResult> workerTask)
    {
        try
        {
            await workerTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.WriteException("Focused composer abandoned worker", exception);
        }
        finally
        {
            _gate.Release();
        }
    }
}
