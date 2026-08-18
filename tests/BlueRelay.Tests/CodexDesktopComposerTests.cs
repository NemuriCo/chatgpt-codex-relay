using BlueRelay.Services.Bridges;
using BlueRelay.Services.Desktop;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexDesktopComposerTests
{
    [TestMethod]
    public void SelectsOnlyUnambiguousOpenAiComposer()
    {
        var candidate = Candidate(
            handle: 100,
            controlType: "Edit",
            automationId: "message-composer",
            name: "Message editor",
            supportsValuePattern: true,
            className: "ProseMirror ProseMirror-focused");

        var selected = CodexComposerCandidateSelector.TrySelect([candidate], out var result);

        Assert.IsTrue(selected);
        Assert.AreSame(candidate, result);
    }

    [TestMethod]
    public void RejectsAmbiguousComposersAndDifferentOpenAiWindows()
    {
        var first = Candidate(100, "Edit", "input-one", "Prompt", supportsValuePattern: true);
        var second = Candidate(100, "Document", "input-two", "Message", supportsValuePattern: true);
        var otherWindow = Candidate(200, "Document", "message-composer", "Message editor", supportsValuePattern: true);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([first, second], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([first, otherWindow], out _));
    }

    [TestMethod]
    public void RejectsControlsThatAreNotSafeEditableOpenAiCandidates()
    {
        var nonOpenAi = Candidate(100, "Edit", "message-composer", "Message editor", supportsValuePattern: true, isOpenAiWindow: false);
        var readOnly = Candidate(
            100,
            "Document",
            "message-composer",
            "Message editor",
            supportsValuePattern: true,
            isReadOnly: true,
            className: "ProseMirror");
        var sendButton = Candidate(100, "Edit", "send-input", "Send", supportsValuePattern: true);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([nonOpenAi], out _));
        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([readOnly], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([sendButton], out _));
    }

    [TestMethod]
    public void UsesParentAutomationHierarchyAsAComposerSignal()
    {
        var candidate = Candidate(
            100,
            "Document",
            "",
            "",
            supportsValuePattern: false,
            supportsTextPattern: true,
            parentHierarchy: "Pane#composer-container > Document",
            frameworkId: "Win32");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out var selected));
        Assert.AreSame(candidate, selected);
    }

    [TestMethod]
    public void ComposesFullTaskWithUnicodeUserNoteWithoutSending()
    {
        const string note = "顺便检查按钮文字 😊";
        const string payload = "# CODEX_TASK\n\n```csharp\nvar path = \"C:\\\\Projects\\\\BlueProject\";\n```";

        var result = RelayPromptComposer.Compose(note, payload);

        Assert.AreEqual(
            "用户补充：\n顺便检查按钮文字 😊\n\n完整任务：\n# CODEX_TASK\n\n```csharp\nvar path = \"C:\\\\Projects\\\\BlueProject\";\n```",
            result);
    }

    [TestMethod]
    public void ClipboardRestoreFailureIsExposedWithoutPretendingInjectionSucceeded()
    {
        var result = CodexComposerInjectionResult.Failed(
            "codex_composer_injection_failed",
            "failed",
            clipboardRestoreFailed: true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.ClipboardRestoreFailed);
    }

    [TestMethod]
    public void RealChromeProseMirrorComposerIsPreferred()
    {
        var composer = Candidate(
            100,
            "Edit",
            "",
            "随心输入",
            supportsValuePattern: true,
            className: "ProseMirror ProseMirror-focused",
            frameworkId: "Chrome");
        var unrelatedChromeEdit = Candidate(
            100,
            "Edit",
            "toolbar-input",
            "Address bar",
            supportsValuePattern: true,
            className: "Chrome_RenderWidgetHostHWND",
            frameworkId: "Chrome",
            parentHierarchy: "Pane#toolbar");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([composer, unrelatedChromeEdit], out var selected));
        Assert.AreSame(composer, selected);
        Assert.IsTrue(CodexComposerCandidateSelector.IsHighConfidence(composer));
        Assert.IsFalse(CodexComposerCandidateSelector.IsHighConfidence(unrelatedChromeEdit));
    }

    [TestMethod]
    public void ProseMirrorClassTokenSurvivesFocusAndBuildHashChanges()
    {
        var candidate = Candidate(
            100,
            "Edit",
            "",
            "Type your message",
            supportsValuePattern: true,
            className: "ProseMirror",
            frameworkId: "Chrome",
            parentHierarchy: "Group@_RichTextInput_newhash > Pane@_ComposerLayoutRoot_otherhash");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out _));
    }

    [TestMethod]
    public void CustomComposerSurfaceWithEditableParentIsRecognized()
    {
        var candidate = Candidate(
            100,
            "Custom",
            "",
            "",
            supportsValuePattern: false,
            supportsTextPattern: true,
            className: "ComposerSurface",
            frameworkId: "Chrome",
            parentHierarchy: "Edit@ProseMirror > Group@ComposerLayoutRoot");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out _));
    }

    [TestMethod]
    public void ReadOnlyValuePatternRemainsAnExactFallbackCandidate()
    {
        var candidate = Candidate(
            100,
            "Edit",
            "",
            "localized placeholder",
            supportsValuePattern: true,
            isReadOnly: true,
            className: "ProseMirror",
            frameworkId: "Chrome");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([candidate], out var selected));
        Assert.IsTrue(selected!.IsValueReadOnly);
    }

    [TestMethod]
    public void WritableValuePatternIsPreferredOverReadOnlyCandidate()
    {
        var writable = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror");
        var readOnly = writable with { IsValueReadOnly = true };

        Assert.IsTrue(CodexComposerCandidateSelector.Score(writable) > CodexComposerCandidateSelector.Score(readOnly));
    }

    [TestMethod]
    public void OffscreenAndDisabledProseMirrorCandidatesAreRejected()
    {
        var offscreen = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror",
            isOffscreen: true);
        var disabled = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror",
            isEnabled: false);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([offscreen], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([disabled], out _));
    }

    [TestMethod]
    public void MultipleEditCandidatesAreResolvedByComposerConfidence()
    {
        var composer = Candidate(
            100,
            "Edit",
            "",
            "",
            supportsValuePattern: true,
            className: "ProseMirror");
        var weakerEdit = Candidate(
            100,
            "Edit",
            "message-input",
            "",
            supportsValuePattern: true,
            className: "Chrome_RenderWidgetHostHWND",
            parentHierarchy: "Group@RichTextInput");

        Assert.IsTrue(CodexComposerCandidateSelector.TrySelect([weakerEdit, composer], out var selected));
        Assert.AreSame(composer, selected);
    }

    [TestMethod]
    public void ExistingComposerContentRequiresExplicitReplacementConfirmation()
    {
        Assert.IsFalse(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.Empty,
            allowReplacingExistingText: false));
        Assert.IsTrue(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.HasContent,
            allowReplacingExistingText: false));
        Assert.IsTrue(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.Unknown,
            allowReplacingExistingText: false));
        Assert.IsFalse(CodexComposerContentGuard.RequiresConfirmation(
            CodexComposerContentState.HasContent,
            allowReplacingExistingText: true));
    }

    [TestMethod]
    public void FocusedChromeNodeCanResolveCodexWindowByPidWithoutItsOwnHwnd()
    {
        var candidates = new[]
        {
            new CodexDesktopWindowCandidate(
                23280,
                new IntPtr(0x1234),
                "Blue project",
                "Chrome_WidgetWin_1",
                "Codex",
                @"C:\Program Files\Codex\Codex.exe",
                IsForeground: true)
        };

        Assert.IsTrue(CodexDesktopWindowOwnership.TrySelectForProcess(23280, candidates, out var selected));
        Assert.AreEqual(new IntPtr(0x1234), selected!.Handle);
    }

    [TestMethod]
    public void FocusedPidMismatchIsRejected()
    {
        var candidates = new[]
        {
            new CodexDesktopWindowCandidate(
                23281,
                new IntPtr(0x1234),
                "Codex",
                "Chrome_WidgetWin_1",
                "Codex",
                @"C:\Program Files\Codex\Codex.exe")
        };

        Assert.IsFalse(CodexDesktopWindowOwnership.TrySelectForProcess(23280, candidates, out _));
    }

    [TestMethod]
    public void MultipleCodexWindowsRequireSafeScoringResolution()
    {
        var ambiguous = new[]
        {
            new CodexDesktopWindowCandidate(23280, new IntPtr(0x1234), "Codex", "Chrome_WidgetWin_1", "Codex", @"C:\Codex.exe"),
            new CodexDesktopWindowCandidate(23280, new IntPtr(0x5678), "Codex", "Chrome_WidgetWin_1", "Codex", @"C:\Codex.exe")
        };
        var foreground = ambiguous[1] with { IsForeground = true };

        Assert.IsFalse(CodexDesktopWindowOwnership.TrySelectForProcess(23280, ambiguous, out _));
        Assert.IsTrue(CodexDesktopWindowOwnership.TrySelectForProcess(23280, [ambiguous[0], foreground], out var selected));
        Assert.AreEqual(foreground.Handle, selected!.Handle);
    }

    [TestMethod]
    public async Task SlowComposerWorkerTimesOutWithoutBlockingCaller()
    {
        var coordinator = CreateCoordinator(TimeSpan.FromMilliseconds(60));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await coordinator.RunAsync(() =>
        {
            Thread.Sleep(300);
            return CodexComposerInjectionResult.Filled("filled");
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual("codex_composer_probe_timeout", result.Code);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Thread.Sleep(350);
    }

    [TestMethod]
    public async Task ConcurrentComposerOperationsAreRejectedWhileWorkerIsActive()
    {
        using var workerStarted = new ManualResetEventSlim(false);
        using var releaseWorker = new ManualResetEventSlim(false);
        var coordinator = CreateCoordinator(TimeSpan.FromSeconds(2));

        var first = coordinator.RunAsync(() =>
        {
            workerStarted.Set();
            releaseWorker.Wait(TimeSpan.FromSeconds(1));
            return CodexComposerInjectionResult.Filled("first");
        });

        Assert.IsTrue(workerStarted.Wait(TimeSpan.FromSeconds(1)));
        var second = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("second"));

        Assert.IsFalse(second.Success);
        Assert.AreEqual("codex_composer_busy", second.Code);
        releaseWorker.Set();
        var firstResult = await first;
        Assert.IsTrue(firstResult.Success);
    }

    [TestMethod]
    public async Task WorkerExceptionRestoresOperationGate()
    {
        var coordinator = CreateCoordinator(TimeSpan.FromSeconds(1));

        var failed = await coordinator.RunAsync(() => throw new InvalidOperationException("fake probe failure"));
        var recovered = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("recovered"));

        Assert.IsFalse(failed.Success);
        Assert.AreEqual("codex_composer_injection_failed", failed.Code);
        Assert.IsTrue(recovered.Success);
    }

    [TestMethod]
    public async Task CancellationReturnsWithoutReleasingGateUntilWorkerCompletes()
    {
        using var workerStarted = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        var coordinator = CreateCoordinator(TimeSpan.FromSeconds(2));

        var pending = coordinator.RunAsync(() =>
        {
            workerStarted.Set();
            Thread.Sleep(250);
            return CodexComposerInjectionResult.Filled("late");
        }, cancellation.Token);

        Assert.IsTrue(workerStarted.Wait(TimeSpan.FromSeconds(1)));
        cancellation.Cancel();
        var cancelled = await pending;

        Assert.IsFalse(cancelled.Success);
        Assert.AreEqual("codex_composer_cancelled", cancelled.Code);
        var busyWhileWorkerFinishes = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("busy"));
        Assert.AreEqual("codex_composer_busy", busyWhileWorkerFinishes.Code);

        Thread.Sleep(300);
        var recovered = await coordinator.RunAsync(() => CodexComposerInjectionResult.Filled("recovered"));
        Assert.IsTrue(recovered.Success);
    }

    [TestMethod]
    public async Task StaWorkerUsesIndependentStaThread()
    {
        var apartmentState = await StaAutomationWorker.RunAsync(
            () => Thread.CurrentThread.GetApartmentState());

        Assert.AreEqual(ApartmentState.STA, apartmentState);
    }

    [TestMethod]
    public void FocusedProbeDisplayContainsStructureButNoValuePayload()
    {
        var metadata = new FocusedComposerElementMetadata(
            42,
            new IntPtr(100),
            "Document",
            "document",
            "message-editor",
            "Chrome_RenderWidgetHostHWND",
            "随心输入",
            "Chromium",
            true,
            true,
            true,
            false,
            new UiAutomationBounds(10, 700, 600, 80),
            ["ValuePattern", "TextPattern", "TextPattern2"],
            false);
        var result = new FocusedComposerProbeResult(
            true,
            "focused_codex_element",
            "Focused element belongs to Codex Desktop.",
            metadata,
            [],
            new FocusedComposerWindowMetadata(42, new IntPtr(100), "Codex", "Chrome_WidgetWin_1", "Codex", true),
            TimeSpan.FromMilliseconds(12));

        var display = result.ToDisplayText();

        StringAssert.Contains(display, "ControlType=Document");
        StringAssert.Contains(display, "FrameworkId=Chromium");
        StringAssert.Contains(display, "Patterns=[ValuePattern, TextPattern, TextPattern2]");
        StringAssert.Contains(display, "Name=随心输入");
        StringAssert.Contains(display, "ValuePatternIsReadOnly=False");
        Assert.IsFalse(display.Contains("Value=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void FocusedProbeParentDepthIsExplicitlyBounded()
    {
        Assert.AreEqual(10, FocusedComposerProbeService.MaxParentDepth);
    }

    [TestMethod]
    public async Task SlowFocusedProbeTimesOutWithoutBlockingCaller()
    {
        var service = new FocusedComposerProbeService(
            TimeSpan.FromMilliseconds(60),
            _ => Task.Run(() =>
            {
                Thread.Sleep(300);
                return FocusedComposerProbeResult.Failed(
                    "fake_completed_late",
                    "late",
                    TimeSpan.FromMilliseconds(300));
            }));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await service.ProbeAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual("focused_probe_timeout", result.Code);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
        Thread.Sleep(300);
    }

    private static CodexComposerOperationCoordinator CreateCoordinator(TimeSpan timeout)
    {
        return new CodexComposerOperationCoordinator(
            timeout,
            operation => Task.Run(operation));
    }

    private static CodexComposerCandidate Candidate(
        long handle,
        string controlType,
        string automationId,
        string name,
        bool supportsValuePattern,
        bool isOpenAiWindow = true,
        bool isReadOnly = false,
        string parentHierarchy = "Pane#conversation > Document#message-editor",
        bool supportsTextPattern = false,
        string className = "",
        string frameworkId = "Chrome",
        bool isEnabled = true,
        bool isOffscreen = false)
    {
        var bounds = new UiAutomationBounds(10, 700, 600, 80);
        var metadata = new UiAutomationMetadata(
            new IntPtr(handle),
            42,
            "Codex",
            controlType,
            automationId,
            name,
            className,
            frameworkId,
            isEnabled,
            true,
            isOffscreen,
            bounds,
            new UiAutomationBounds(0, 0, 800, 800),
            parentHierarchy,
            true,
            false);
        return new CodexComposerCandidate(
            metadata,
            isOpenAiWindow,
            supportsValuePattern,
            isReadOnly,
            0,
            supportsTextPattern);
    }
}
