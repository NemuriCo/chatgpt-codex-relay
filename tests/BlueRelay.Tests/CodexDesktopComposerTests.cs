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
            controlType: "Document",
            automationId: "message-composer",
            name: "Message editor",
            supportsValuePattern: true);

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
        var readOnly = Candidate(100, "Document", "message-composer", "Message editor", supportsValuePattern: true, isReadOnly: true);
        var sendButton = Candidate(100, "Edit", "send-input", "Send", supportsValuePattern: true);

        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([nonOpenAi], out _));
        Assert.IsFalse(CodexComposerCandidateSelector.TrySelect([readOnly], out _));
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
            parentHierarchy: "Pane#composer-container > Document");

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
            ["TextPattern", "TextPattern2"]);
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
        StringAssert.Contains(display, "Patterns=[TextPattern, TextPattern2]");
        StringAssert.Contains(display, "Name=随心输入");
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
        string parentHierarchy = "Pane#conversation > Document#message-editor")
    {
        var bounds = new UiAutomationBounds(10, 700, 600, 80);
        var metadata = new UiAutomationMetadata(
            new IntPtr(handle),
            42,
            "Codex",
            controlType,
            automationId,
            name,
            "",
            "Chrome",
            true,
            true,
            false,
            bounds,
            new UiAutomationBounds(0, 0, 800, 800),
            parentHierarchy,
            true,
            false);
        return new CodexComposerCandidate(metadata, isOpenAiWindow, supportsValuePattern, isReadOnly, 0);
    }
}
