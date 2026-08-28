using System.Runtime.InteropServices;
using System.Windows.Automation;
using BlueRelay.Diagnostics;

namespace BlueRelay.Services.Desktop;

public sealed class WindowsCodexDesktopComposerSender : ICodexDesktopComposerSender
{
    private const int MaxComposerNodes = 3000;
    private const int MaxComposerDepth = 48;
    private const int MaxSendSearchNodes = 512;
    private const int MaxSendSearchDepth = 8;
    private const int MaxParentDepth = 4;
    private const int LegacyIAccessiblePatternId = 10018;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public async Task<CodexComposerSendResult> SendAsync(
        CodexFillReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        cancellationToken.ThrowIfCancellationRequested();

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerTask = StaAutomationWorker.RunAsync(
            () => SendOnWorker(receipt, operationCancellation.Token),
            ThreadPriority.Normal,
            "BlueRelay Codex Send UI Automation");
        var timeoutTask = Task.Delay(SendTimeout);
        var cancellationTask = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan);
        var completedTask = await Task.WhenAny(workerTask, timeoutTask, cancellationTask).ConfigureAwait(false);

        if (completedTask == workerTask)
        {
            return await workerTask.ConfigureAwait(false);
        }

        operationCancellation.Cancel();
        if (completedTask == cancellationTask)
        {
            _ = ObserveWorkerAsync(workerTask, "Codex send cancellation worker");
            throw new OperationCanceledException(cancellationToken);
        }

        StartupDiagnostics.Write("codex_send_probe_timeout");
        _ = ObserveWorkerAsync(workerTask, "Codex send timeout worker");
        return CodexComposerSendResult.Failed(
            "codex_send_probe_timeout",
            "The Codex send operation timed out. Please retry.");
    }

    private static CodexComposerSendResult SendOnWorker(
        CodexFillReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.WindowHandle == IntPtr.Zero || receipt.ProcessId <= 0)
        {
            return CodexComposerSendResult.Failed(
                "codex_send_window_changed",
                "The original Codex window is no longer available.");
        }

        if (!NativeMethods.IsWindow(receipt.WindowHandle))
        {
            return CodexComposerSendResult.Failed(
                "codex_send_window_changed",
                "The original Codex window has changed.");
        }

        AutomationElement window;
        try
        {
            window = AutomationElement.FromHandle(receipt.WindowHandle);
            if (window.Current.ProcessId != receipt.ProcessId)
            {
                return CodexComposerSendResult.Failed(
                    "codex_send_window_changed",
                    "The original Codex window has changed.");
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            StartupDiagnostics.WriteException("Codex send window reacquire", exception);
            return CodexComposerSendResult.Failed(
                "codex_send_window_changed",
                "The original Codex window is no longer available.");
        }

        var composer = FindComposer(window, receipt, cancellationToken);
        if (composer is null)
        {
            return CodexComposerSendResult.Failed(
                "codex_composer_not_found",
                "The filled Codex input box is no longer available.");
        }

        var content = ReadComposerContent(composer, cancellationToken);
        StartupDiagnostics.Write(
            $"codex_send_content composerEmpty={content.State == CodexComposerContentState.Empty} " +
            $"contentAvailable={content.IsAvailable} " +
            $"attachmentOrReference={content.HasAttachmentOrReference}");
        if (!content.HasSendableContent)
        {
            return CodexComposerSendResult.Failed(
                content.IsAvailable && content.State == CodexComposerContentState.Empty
                    ? "codex_send_composer_empty"
                    : "codex_send_composer_content_unknown",
                content.IsAvailable && content.State == CodexComposerContentState.Empty
                    ? "The Codex input box is empty. BlueRelay did not send again to avoid a duplicate."
                    : "BlueRelay could not safely verify the current Codex input box content.",
                composerEmpty: content.State == CodexComposerContentState.Empty,
                hasAttachmentOrReference: content.HasAttachmentOrReference);
        }

        SendButtonLookup lookup;
        try
        {
            lookup = FindSendButton(composer, cancellationToken);
        }
        catch (ArgumentNullException exception)
        {
            StartupDiagnostics.WriteException("codex_send_pattern_internal_error", exception);
            return CodexComposerSendResult.Failed(
                "sender_internal_pattern_error",
                "BlueRelay encountered an internal UI Automation pattern error.",
                composerEmpty: false,
                hasAttachmentOrReference: content.HasAttachmentOrReference);
        }

        WriteButtonSearchDiagnostics(lookup);
        if (lookup.Selected is null)
        {
            return CodexComposerSendResult.Failed(
                "codex_send_button_not_found",
                "No usable Codex send button was found. Make sure the current input can be sent and retry.",
                composerEmpty: false,
                hasAttachmentOrReference: content.HasAttachmentOrReference,
                candidateCount: lookup.Candidates.Count,
                locatorMethod: lookup.LocatorMethod,
                buttonCandidates: lookup.Candidates);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var current = lookup.Selected.Current;
            if (!current.IsEnabled || current.IsOffscreen ||
                !TryGetInvokePattern(lookup.Selected, out var invokePattern) ||
                invokePattern is null)
            {
                StartupDiagnostics.Write("codex_send_invoke attempted=false reason=button_not_invokable");
                return CodexComposerSendResult.Failed(
                    "codex_send_button_not_found",
                    "The Codex send button is not currently available.",
                    hasAttachmentOrReference: content.HasAttachmentOrReference,
                    candidateCount: lookup.Candidates.Count,
                    locatorMethod: lookup.LocatorMethod,
                    buttonCandidates: lookup.Candidates);
            }

            StartupDiagnostics.Write("codex_send_invoke attempted=true");
            invokePattern.Invoke();
            StartupDiagnostics.Write("codex_send_invoke attempted=true success=true");
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            StartupDiagnostics.WriteException("Codex send InvokePattern.Invoke", exception);
            StartupDiagnostics.Write("codex_send_invoke attempted=true success=false");
            return CodexComposerSendResult.Failed(
                "codex_send_invoke_failed",
                "BlueRelay could not trigger Codex send. Check Codex and retry.",
                hasAttachmentOrReference: content.HasAttachmentOrReference,
                candidateCount: lookup.Candidates.Count,
                locatorMethod: lookup.LocatorMethod,
                buttonCandidates: lookup.Candidates);
        }

        var postCheck = CapturePostSendCheck(composer, lookup.Selected, cancellationToken);
        StartupDiagnostics.Write(
            $"codex_send_postcheck composerEmpty={postCheck.ComposerEmpty} " +
            $"buttonPresent={postCheck.SendButtonPresent} " +
            $"buttonEnabled={postCheck.SendButtonEnabled}");
        return new CodexComposerSendResult(
            true,
            "codex_send_invoked",
            "Codex send was invoked.",
            InvokeAttempted: true,
            InvokeSucceeded: true,
            HasAttachmentOrReference: content.HasAttachmentOrReference,
            CandidateCount: lookup.Candidates.Count,
            Matched: true,
            LocatorMethod: lookup.LocatorMethod,
            ButtonCandidates: lookup.Candidates,
            PostCheck: postCheck);
    }

    private static AutomationElement? FindComposer(
        AutomationElement window,
        CodexFillReceipt receipt,
        CancellationToken cancellationToken)
    {
        return FindComposerInView(window, receipt, TreeWalker.ControlViewWalker, cancellationToken) ??
               FindComposerInView(window, receipt, TreeWalker.RawViewWalker, cancellationToken);
    }

    private static AutomationElement? FindComposerInView(
        AutomationElement window,
        CodexFillReceipt receipt,
        TreeWalker walker,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((window, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxComposerNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            if (IsComposerMatch(element, receipt))
            {
                return element;
            }

            if (depth >= MaxComposerDepth)
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

    private static bool IsComposerMatch(AutomationElement element, CodexFillReceipt receipt)
    {
        try
        {
            var current = element.Current;
            var type = GetControlTypeName(current.ControlType);
            var editable = type is "Edit" or "Document" or "Custom";
            if (!editable || !current.IsEnabled || current.IsOffscreen ||
                current.BoundingRectangle.Width <= 0 || current.BoundingRectangle.Height <= 0)
            {
                return false;
            }

            var classMatches = !string.IsNullOrWhiteSpace(receipt.ComposerClassName) &&
                               string.Equals(current.ClassName, receipt.ComposerClassName, StringComparison.OrdinalIgnoreCase);
            var automationIdMatches = !string.IsNullOrWhiteSpace(receipt.ComposerAutomationId) &&
                                      string.Equals(current.AutomationId, receipt.ComposerAutomationId, StringComparison.OrdinalIgnoreCase);
            var proseMirror = ContainsToken(current.ClassName, "ProseMirror");
            var chromium = IsChromium(current.FrameworkId);
            return chromium && (automationIdMatches || classMatches || proseMirror);
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return false;
        }
    }

    private static CodexComposerContentProbe ReadComposerContent(
        AutomationElement composer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var textAvailable = false;
        string? text = null;
        var valueAvailable = false;
        string? value = null;
        string accessibilityName = string.Empty;
        try
        {
            if (composer.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                text = textPattern.DocumentRange.GetText(-1);
                textAvailable = true;
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        try
        {
            if (composer.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                value = valuePattern.Current.Value;
                valueAvailable = true;
            }
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        string className = string.Empty;
        try
        {
            var current = composer.Current;
            className = current.ClassName ?? string.Empty;
            accessibilityName = current.Name ?? string.Empty;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        var state = CodexComposerContentGuard.DetermineContentState(
            ContainsToken(className, "ProseMirror"),
            textAvailable,
            text,
            valueAvailable,
            value,
            accessibilityName);
        var attachmentOrReference = HasAttachmentOrReference(composer, cancellationToken);
        return new CodexComposerContentProbe(
            textAvailable || valueAvailable,
            state,
            attachmentOrReference);
    }

    private static bool HasAttachmentOrReference(
        AutomationElement composer,
        CancellationToken cancellationToken)
    {
        AutomationElement? scope = composer;
        try
        {
            scope = TreeWalker.RawViewWalker.GetParent(composer) ?? composer;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
        }

        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((scope, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxSendSearchNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            try
            {
                var current = element.Current;
                var metadata = string.Join(
                    " ",
                    current.AutomationId,
                    current.ClassName,
                    current.Name,
                    current.LocalizedControlType,
                    current.HelpText,
                    current.ItemStatus,
                    current.ItemType);
                var controlType = GetControlTypeName(current.ControlType);
                if (element != composer && !current.IsOffscreen &&
                    current.BoundingRectangle.Width > 0 &&
                    current.BoundingRectangle.Height > 0 &&
                    controlType is "Group" or "Custom" or "Pane" or "ListItem" or "DataItem" or "Document" &&
                    ContainsAny(metadata, "attachment", "upload", "file", "pasted", "reference", "card"))
                {
                    return true;
                }
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }

            if (depth < MaxSendSearchDepth)
            {
                foreach (var child in GetChildren(element, TreeWalker.RawViewWalker, 128))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return false;
    }

    private static SendButtonLookup FindSendButton(
        AutomationElement composer,
        CancellationToken cancellationToken)
    {
        AutomationElement? ancestor = composer;
        var allCandidates = new List<(AutomationElement Element, CodexSendButtonMetadata Metadata)>();
        var visitedNodes = 0;
        for (var parentDepth = 0; parentDepth <= MaxParentDepth && ancestor is not null; parentDepth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var controlResult = FindButtonsInScope(
                ancestor,
                TreeWalker.ControlViewWalker,
                cancellationToken);
            visitedNodes += controlResult.VisitedNodes;
            allCandidates.AddRange(controlResult.Candidates);
            if (TrySelectButton(controlResult.Candidates, out var selected))
            {
                return new SendButtonLookup(
                    selected,
                    allCandidates.Select(item => item.Metadata).ToList(),
                    visitedNodes,
                    $"control_view_parent_{parentDepth}");
            }

            var rawResult = FindButtonsInScope(
                ancestor,
                TreeWalker.RawViewWalker,
                cancellationToken);
            visitedNodes += rawResult.VisitedNodes;
            allCandidates.AddRange(rawResult.Candidates);
            if (TrySelectButton(rawResult.Candidates, out selected))
            {
                return new SendButtonLookup(
                    selected,
                    allCandidates.Select(item => item.Metadata).ToList(),
                    visitedNodes,
                    $"raw_view_parent_{parentDepth}");
            }

            try
            {
                ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
                ancestor = null;
            }
        }

        return new SendButtonLookup(
            null,
            allCandidates.Select(item => item.Metadata).Distinct().ToList(),
            visitedNodes,
            "bounded_parent_walk");
    }

    private static ButtonScopeScanResult FindButtonsInScope(
        AutomationElement root,
        TreeWalker walker,
        CancellationToken cancellationToken)
    {
        var result = new List<(AutomationElement Element, CodexSendButtonMetadata Metadata)>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxSendSearchNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            try
            {
                var current = element.Current;
                if (current.ControlType == ControlType.Button)
                {
                    var metadata = CreateButtonMetadata(element, current);
                    result.Add((element, metadata));
                }
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }

            if (depth < MaxSendSearchDepth)
            {
                foreach (var child in GetChildren(element, walker, 128))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return new ButtonScopeScanResult(result, visited);
    }

    private static CodexSendButtonMetadata CreateButtonMetadata(
        AutomationElement element,
        AutomationElement.AutomationElementInformation current)
    {
        var invokeAvailable = HasPattern(element, InvokePattern.Pattern);
        var legacyPattern = AutomationPattern.LookupById(LegacyIAccessiblePatternId);
        if (legacyPattern is null)
        {
            StartupDiagnostics.Write(
                "codex_send_pattern_probe pattern=LegacyIAccessiblePattern " +
                "available=false reason=identifier_unavailable");
        }

        var legacyAvailable = HasPattern(element, legacyPattern);
        return new CodexSendButtonMetadata(
            "Button",
            current.LocalizedControlType ?? string.Empty,
            current.AutomationId ?? string.Empty,
            current.ClassName ?? string.Empty,
            current.FrameworkId ?? string.Empty,
            current.IsEnabled,
            current.IsOffscreen,
            invokeAvailable,
            legacyAvailable,
            ToBounds(current.BoundingRectangle),
            current.Name ?? string.Empty);
    }

    private static bool TrySelectButton(
        IReadOnlyList<(AutomationElement Element, CodexSendButtonMetadata Metadata)> candidates,
        out AutomationElement? selected)
    {
        selected = null;
        if (!CodexSendButtonSelector.TrySelect(candidates.Select(item => item.Metadata).ToList(), out var selectedMetadata) ||
            selectedMetadata is null)
        {
            return false;
        }

        selected = candidates
            .FirstOrDefault(item => ReferenceEquals(item.Metadata, selectedMetadata)).Element;
        return selected is not null;
    }

    private static CodexSendPostCheck CapturePostSendCheck(
        AutomationElement composer,
        AutomationElement sendButton,
        CancellationToken cancellationToken)
    {
        var delays = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(450)
        };
        var last = new CodexSendPostCheck(false, true, true);
        foreach (var delay in delays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(delay);
            var content = ReadComposerContent(composer, cancellationToken);
            var present = false;
            var enabled = false;
            try
            {
                var current = sendButton.Current;
                present = true;
                enabled = current.IsEnabled && !current.IsOffscreen;
            }
            catch (Exception exception) when (IsUiAutomationFailure(exception))
            {
            }

            last = new CodexSendPostCheck(
                content.IsAvailable && content.State == CodexComposerContentState.Empty,
                present,
                enabled);
        }

        return last;
    }

    private static void WriteButtonSearchDiagnostics(SendButtonLookup lookup)
    {
        var matched = lookup.Selected is not null;
        StartupDiagnostics.Write(
            $"codex_send_button_search scopeMethod={Sanitize(lookup.LocatorMethod)} " +
            $"visitedNodes={lookup.VisitedNodes} candidateCount={lookup.Candidates.Count} " +
            $"matchedCount={(matched ? 1 : 0)} matched={matched} " +
            $"locatorMethod={Sanitize(lookup.LocatorMethod)}");
        for (var index = 0; index < lookup.Candidates.Count; index++)
        {
            var candidate = lookup.Candidates[index];
            StartupDiagnostics.Write(
                $"codex_send_button_candidate candidateIndex={index} " +
                $"controlType={Sanitize(candidate.ControlType)} " +
                $"localizedControlType={Sanitize(candidate.LocalizedControlType)} " +
                $"automationId={Sanitize(candidate.AutomationId)} " +
                $"className={Sanitize(candidate.ClassName)} " +
                $"frameworkId={Sanitize(candidate.FrameworkId)} " +
                $"enabled={candidate.IsEnabled} offscreen={candidate.IsOffscreen} " +
                $"invokePattern={candidate.InvokePatternAvailable} " +
                $"legacyIAccessiblePattern={candidate.LegacyIAccessiblePatternAvailable} " +
                $"bounds={candidate.Bounds.Left:0.##},{candidate.Bounds.Top:0.##}," +
                $"{candidate.Bounds.Width:0.##},{candidate.Bounds.Height:0.##} " +
                $"name={Sanitize(candidate.Name)}");
        }
    }

    private static IReadOnlyList<AutomationElement> GetChildren(
        AutomationElement element,
        TreeWalker walker,
        int maxSiblings)
    {
        var children = new List<AutomationElement>();
        try
        {
            var child = walker.GetFirstChild(element);
            while (child is not null && children.Count < maxSiblings)
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

    private static bool TryGetInvokePattern(
        AutomationElement element,
        out InvokePattern? invokePattern)
    {
        invokePattern = null;
        var pattern = InvokePattern.Pattern;
        if (pattern is null)
        {
            StartupDiagnostics.Write(
                "codex_send_pattern_probe pattern=InvokePattern " +
                "available=false reason=identifier_unavailable");
            return false;
        }

        try
        {
            if (!element.TryGetCurrentPattern(pattern, out var patternObject) ||
                patternObject is not InvokePattern resolvedPattern)
            {
                return false;
            }

            invokePattern = resolvedPattern;
            return true;
        }
        catch (ArgumentNullException exception)
        {
            StartupDiagnostics.WriteException("codex_send_invoke_pattern_probe", exception);
            return false;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            StartupDiagnostics.WriteException("codex_send_invoke_pattern_probe", exception);
            return false;
        }
    }

    private static bool HasPattern(AutomationElement element, AutomationPattern? pattern)
    {
        if (pattern is null)
        {
            return false;
        }

        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch (ArgumentNullException exception)
        {
            StartupDiagnostics.WriteException("codex_send_pattern_probe", exception);
            return false;
        }
        catch (Exception exception) when (IsUiAutomationFailure(exception))
        {
            return false;
        }
    }

    private static UiAutomationBounds ToBounds(System.Windows.Rect bounds) =>
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

        if (controlType == ControlType.Group)
        {
            return "Group";
        }

        if (controlType == ControlType.Pane)
        {
            return "Pane";
        }

        if (controlType == ControlType.ListItem)
        {
            return "ListItem";
        }

        if (controlType == ControlType.DataItem)
        {
            return "DataItem";
        }

        if (controlType == ControlType.Button)
        {
            return "Button";
        }

        return controlType?.ProgrammaticName ?? string.Empty;
    }

    private static bool ContainsToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsAny(string? value, params string[] tokens) =>
        tokens.Any(token => value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsChromium(string? frameworkId) =>
        string.Equals(frameworkId, "Chrome", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frameworkId, "Chromium", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frameworkId, "WebView2", StringComparison.OrdinalIgnoreCase);

    private static bool IsUiAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or COMException or InvalidOperationException;

    private static string Sanitize(string? value) =>
        (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();

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

    private sealed record SendButtonLookup(
        AutomationElement? Selected,
        IReadOnlyList<CodexSendButtonMetadata> Candidates,
        int VisitedNodes,
        string LocatorMethod);

    private sealed record ButtonScopeScanResult(
        IReadOnlyList<(AutomationElement Element, CodexSendButtonMetadata Metadata)> Candidates,
        int VisitedNodes);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr handle);
    }
}
