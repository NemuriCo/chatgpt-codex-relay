using BlueRelay.Services.Desktop;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexRunObserverTests
{
    [TestMethod]
    public void Accumulator_ExcludesPreSendHistoricalAssistantBlocks()
    {
        const string historical = "historical assistant response";
        var baseline = new CodexRunBaseline(
            true,
            1,
            [new CodexRunBlockBaseline(
                0,
                "history-fingerprint",
                "history-parent",
                historical.Length,
                CodexRunOutputAccumulator.ComputeTextPrefixHash(historical))]);
        var accumulator = new CodexRunOutputAccumulator(baseline);

        var outputs = accumulator.Apply(
            [
                new CodexRunBlockObservation(0, "history-fingerprint", "history-parent", historical, true),
                new CodexRunBlockObservation(1, "new-fingerprint", "new-parent", "new assistant response", true)
            ],
            DateTimeOffset.UtcNow);

        Assert.AreEqual(1, outputs.Count);
        Assert.AreEqual("new assistant response", outputs[0].Text);
        Assert.AreEqual(1, accumulator.LastNewOutputCount);
    }

    [TestMethod]
    public void Accumulator_TreatsStreamingGrowthAsOneOutput()
    {
        var accumulator = new CodexRunOutputAccumulator(new CodexRunBaseline(true, 0, []));
        var firstObservedAt = DateTimeOffset.UtcNow;
        var first = accumulator.Apply(
            [new CodexRunBlockObservation(0, "response", "parent", "draft response", true)],
            firstObservedAt);
        var second = accumulator.Apply(
            [new CodexRunBlockObservation(0, "response", "parent", "draft response with more text", true)],
            firstObservedAt.AddSeconds(1));

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual("draft response with more text", second[0].Text);
        Assert.AreEqual(first[0].FirstObservedAtUtc, second[0].FirstObservedAtUtc);
        Assert.AreEqual(1, accumulator.LastChangedOutputCount);
        Assert.AreEqual(0, accumulator.LastNewOutputCount);
    }

    [TestMethod]
    public void Accumulator_ReconcilesRerenderedBlocksAndPreservesDocumentOrder()
    {
        var accumulator = new CodexRunOutputAccumulator(new CodexRunBaseline(true, 0, []));
        var observedAt = DateTimeOffset.UtcNow;
        accumulator.Apply(
            [
                new CodexRunBlockObservation(0, "runtime-independent-a", "parent", "first response with stable text", true),
                new CodexRunBlockObservation(1, "runtime-independent-b", "parent", "second response with stable text", true)
            ],
            observedAt);

        var outputs = accumulator.Apply(
            [
                new CodexRunBlockObservation(1, "rerendered-b", "parent", "second response with stable text and growth", true),
                new CodexRunBlockObservation(0, "rerendered-a", "parent", "first response with stable text and growth", true)
            ],
            observedAt.AddSeconds(1));

        Assert.AreEqual(2, outputs.Count);
        Assert.AreEqual(0, outputs[0].SequenceIndex);
        StringAssert.Contains(outputs[0].Text, "first response");
        Assert.AreEqual(1, outputs[1].SequenceIndex);
        StringAssert.Contains(outputs[1].Text, "second response");
        Assert.AreEqual(0, accumulator.LastNewOutputCount);
    }

    [TestMethod]
    public void Classifier_RequiresCopyAndAssistantActionStructure()
    {
        var structure = new CodexAssistantActionStructure(
            InThreadScope: true,
            InComposer: false,
            HasCopyButton: true,
            ActionButtonCount: 3,
            HasFeedbackAction: true,
            HasBodyContent: true);

        Assert.IsTrue(CodexAssistantBlockClassifier.IsTrustedAssistantBlock(structure));
    }

    [TestMethod]
    public void Classifier_DoesNotPromoteTextWithoutAssistantActionStructure()
    {
        var structure = new CodexAssistantActionStructure(
            InThreadScope: true,
            InComposer: false,
            HasCopyButton: true,
            ActionButtonCount: 1,
            HasFeedbackAction: false,
            HasBodyContent: true);

        Assert.IsFalse(CodexAssistantBlockClassifier.IsTrustedAssistantBlock(structure));
    }

    [TestMethod]
    public void Classifier_RejectsCopyInsideComposerEvenWhenTextExists()
    {
        var structure = new CodexAssistantActionStructure(
            InThreadScope: true,
            InComposer: true,
            HasCopyButton: true,
            ActionButtonCount: 3,
            HasFeedbackAction: true,
            HasBodyContent: true);

        Assert.IsFalse(CodexAssistantBlockClassifier.IsTrustedAssistantBlock(structure));
    }

    [TestMethod]
    public void CompletionTracker_RunningThenDisappearingControlEntersCandidate()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;

        var running = tracker.Observe(CodexRunControlState.Running, 0, 0, 0, start);
        var candidate = tracker.Observe(
            CodexRunControlState.NotRunning,
            1,
            1,
            0,
            start.AddSeconds(1));

        Assert.IsTrue(running.RunningObserved);
        Assert.AreEqual(CodexRunCompletionState.CompletionCandidate, candidate.State);
        Assert.IsTrue(candidate.CompletionCandidate);
        Assert.IsFalse(candidate.IsComplete);
    }

    [TestMethod]
    public void CompletionTracker_ZeroOutputsNeverCompletes()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(1));
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(CodexRunControlState.Running, 0, 0, 0, start);

        var decision = tracker.Observe(
            CodexRunControlState.NotRunning,
            0,
            0,
            0,
            start.AddSeconds(10));

        Assert.IsTrue(decision.CompletionCandidate);
        Assert.IsFalse(decision.IsComplete);
        Assert.AreEqual(CodexRunCompletionState.CompletionCandidate, decision.State);
    }

    [TestMethod]
    public void CompletionTracker_QuietWindowBlocksEarlyCompletion()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(CodexRunControlState.Running, 1, 1, 0, start);

        var decision = tracker.Observe(
            CodexRunControlState.NotRunning,
            1,
            0,
            0,
            start.AddSeconds(2));

        Assert.IsFalse(decision.IsComplete);
        Assert.IsTrue(decision.QuietFor < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void CompletionTracker_OutputGrowthResetsQuietWindow()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(CodexRunControlState.Running, 1, 1, 0, start);
        tracker.Observe(CodexRunControlState.NotRunning, 1, 0, 0, start.AddSeconds(1));

        var growth = tracker.Observe(
            CodexRunControlState.NotRunning,
            1,
            0,
            1,
            start.AddSeconds(5));
        var beforeQuiet = tracker.Observe(
            CodexRunControlState.NotRunning,
            1,
            0,
            0,
            start.AddSeconds(9));
        var complete = tracker.Observe(
            CodexRunControlState.NotRunning,
            1,
            0,
            0,
            start.AddSeconds(10));

        Assert.IsFalse(growth.IsComplete);
        Assert.IsFalse(beforeQuiet.IsComplete);
        Assert.IsTrue(complete.IsComplete);
    }

    [TestMethod]
    public void CompletionTracker_NewSecondOutputResetsQuietWindow()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(CodexRunControlState.Running, 1, 1, 0, start);
        tracker.Observe(CodexRunControlState.NotRunning, 1, 0, 0, start.AddSeconds(1));
        tracker.Observe(CodexRunControlState.NotRunning, 1, 0, 0, start.AddSeconds(5));

        var secondOutput = tracker.Observe(
            CodexRunControlState.NotRunning,
            2,
            1,
            0,
            start.AddSeconds(6));
        var beforeQuiet = tracker.Observe(
            CodexRunControlState.NotRunning,
            2,
            0,
            0,
            start.AddSeconds(10));
        var complete = tracker.Observe(
            CodexRunControlState.NotRunning,
            2,
            0,
            0,
            start.AddSeconds(11));

        Assert.IsFalse(secondOutput.IsComplete);
        Assert.IsFalse(beforeQuiet.IsComplete);
        Assert.IsTrue(complete.IsComplete);
    }

    [TestMethod]
    public void CompletionTracker_RunningReturnCancelsCandidate()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(CodexRunControlState.Running, 1, 1, 0, start);
        tracker.Observe(CodexRunControlState.NotRunning, 1, 0, 0, start.AddSeconds(1));

        var runningAgain = tracker.Observe(
            CodexRunControlState.Running,
            1,
            0,
            0,
            start.AddSeconds(2));

        Assert.IsTrue(runningAgain.RunningReturned);
        Assert.IsFalse(runningAgain.CompletionCandidate);
        Assert.AreEqual(CodexRunCompletionState.RunningObserved, runningAgain.State);
    }

    [TestMethod]
    public void CompletionTracker_StableOutputAndQuietControlCompletes()
    {
        var tracker = new CodexRunCompletionTracker(TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;
        tracker.Observe(CodexRunControlState.Running, 1, 1, 0, start);
        tracker.Observe(CodexRunControlState.NotRunning, 1, 0, 0, start.AddSeconds(1));

        var decision = tracker.Observe(
            CodexRunControlState.NotRunning,
            1,
            0,
            0,
            start.AddSeconds(6));

        Assert.IsTrue(decision.IsComplete);
        Assert.AreEqual(CodexRunCompletionState.Completed, decision.State);
    }

    [TestMethod]
    public void Renderer_SeparatesMultipleOutputsAndLeavesSingleOutputRaw()
    {
        var first = new CodexRunOutput(
            1,
            "b",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            CodexRunOutputKind.AssistantText,
            "second",
            CodexRunCaptureMethod.NativeCopy);
        var second = first with
        {
            SequenceIndex = 0,
            StructuralFingerprint = "a",
            Text = "first"
        };

        Assert.AreEqual("first", CodexRunResultRenderer.Render([second]));
        Assert.AreEqual(
            "[Codex Output 1/2]\r\nfirst\r\n---\r\n\r\n[Codex Output 2/2]\r\nsecond",
            CodexRunResultRenderer.Render([first, second]));
    }

    [TestMethod]
    public async Task WindowsObserverRejectsASecondActiveRunOnTheSameWindow()
    {
        var observer = new WindowsCodexRunObserver();
        var firstReceipt = new CodexRunReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new IntPtr(1),
            42,
            DateTimeOffset.UtcNow,
            CodexComposerInjectionMode.ValuePatternVerified,
            new CodexRunBaseline(true, 0, []));
        var secondReceipt = firstReceipt with { RunId = Guid.NewGuid(), TaskId = Guid.NewGuid() };

        Assert.IsTrue(observer.TryStart(firstReceipt, out var firstHandle));
        Assert.IsNotNull(firstHandle);
        Assert.IsFalse(observer.TryStart(secondReceipt, out _));

        firstHandle!.Cancel();
        var result = await firstHandle.Completion;
        Assert.IsFalse(result.Success);
        Assert.AreEqual("codex_run_cancelled", result.Code);
    }

    [TestMethod]
    public void BoundaryReconciliation_VirtualizedBaselineStillFindsPostBoundaryAssistant()
    {
        var baseline = BaselineWithUser("historical task", assistantCount: 7);
        var accumulator = new CodexRunOutputAccumulator(baseline);
        var observedAt = DateTimeOffset.UtcNow;

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                Assistant("old-0", -500, 0),
                Assistant("old-1", -400, 1),
                Assistant("old-2", -300, 2),
                Assistant("old-3", -200, 3),
                Assistant("old-4", -100, 4),
                User("current task unique", 600, 5),
                Assistant("new response", 650, 6)
            ],
            observedAt,
            Guid.NewGuid(),
            4,
            CodexRunSemanticAnchors.FromText("current task unique"),
            isFlexColumnReverse: true);

        Assert.IsTrue(reconciliation.BoundaryConfirmed);
        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
        Assert.AreEqual("new response", reconciliation.OutputsSafe[0].Text);
    }

    [TestMethod]
    public void BoundaryReconciliation_StructuralFingerprintCollisionDoesNotHideNewAssistant()
    {
        var baseline = BaselineWithUser("old task", assistantCount: 7, fingerprint: "same-structure");
        var accumulator = new CodexRunOutputAccumulator(baseline);

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                Assistant("old-0", -500, 0, "same-structure"),
                Assistant("old-1", -400, 1, "same-structure"),
                Assistant("old-2", -300, 2, "same-structure"),
                Assistant("old-3", -200, 3, "same-structure"),
                Assistant("old-4", -100, 4, "same-structure"),
                User("new task anchor", 600, 5, "same-user-structure"),
                Assistant("OK", 650, 6, "same-structure")
            ],
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            5,
            CodexRunSemanticAnchors.FromText("new task anchor"),
            isFlexColumnReverse: true);

        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
        Assert.AreEqual("OK", reconciliation.OutputsSafe[0].Text);
    }

    [TestMethod]
    public void BoundaryReconciliation_ConfirmsSemanticCurrentUserAndRetainsMetadata()
    {
        var taskId = Guid.NewGuid();
        var anchors = CodexRunSemanticAnchors.FromText("current task semantic anchor");
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("historical task", 1));

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                User("historical task", 100, 0),
                Assistant("old", 150, 1),
                User("current task semantic anchor", 500, 2, "current-user", "current-action"),
                Assistant("new", 550, 3)
            ],
            DateTimeOffset.UtcNow,
            taskId,
            9,
            anchors,
            isFlexColumnReverse: true);

        Assert.IsNotNull(reconciliation.Boundary);
        Assert.AreEqual(taskId, reconciliation.Boundary!.TaskId);
        Assert.AreEqual(9, reconciliation.Boundary.Generation);
        Assert.IsTrue(reconciliation.Boundary.MatchedCurrentTask);
        Assert.IsTrue(reconciliation.Boundary.MatchedAnchorCount > 0);
    }

    [TestMethod]
    public void BoundaryReconciliation_HistoricalMatchingUserIsNotChosenAsCurrentBoundary()
    {
        var taskText = "current task unique anchor";
        var accumulator = new CodexRunOutputAccumulator(
            BaselineWithUser(taskText, assistantCount: 1, fingerprint: "user-history"));

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                User(taskText, 100, 0, "user-history", "user-action"),
                Assistant("old", 150, 1),
                User(taskText + " now", 500, 2, "user-new", "user-action-new"),
                Assistant("new", 550, 3)
            ],
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            1,
            CodexRunSemanticAnchors.FromText(taskText),
            isFlexColumnReverse: true);

        Assert.IsNotNull(reconciliation.Boundary);
        Assert.AreEqual("user-new", reconciliation.Boundary!.StructuralFingerprint);
        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
    }

    [TestMethod]
    public void BoundaryReconciliation_RepeatedIdenticalTaskUsesPostBaselineOrdinal()
    {
        const string taskText = "same task anchor";
        var accumulator = new CodexRunOutputAccumulator(
            BaselineWithUser(taskText, assistantCount: 1));

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                User(taskText, 100, 0, "same-user", "user-action"),
                Assistant("old", 150, 1),
                User(taskText, 300, 2, "same-user", "user-action"),
                Assistant("new", 350, 3)
            ],
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            2,
            CodexRunSemanticAnchors.FromText(taskText),
            isFlexColumnReverse: true);

        Assert.IsNotNull(reconciliation.Boundary);
        Assert.AreEqual(2, reconciliation.Boundary!.Ordinal);
        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
        Assert.AreEqual("new", reconciliation.OutputsSafe[0].Text);
    }

    [TestMethod]
    public void BoundaryReconciliation_ExcludesAssistantBeforeBoundaryAndIncludesAfter()
    {
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("old", 1));

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                Assistant("historical", 100, 0),
                User("run anchor", 200, 1),
                Assistant("current", 300, 2)
            ],
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);

        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
        Assert.AreEqual("current", reconciliation.OutputsSafe[0].Text);
    }

    [TestMethod]
    public void BoundaryReconciliation_OrdersThreeAssistantSlotsAfterBoundary()
    {
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("old", 1));

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                Assistant("third", 500, 4),
                Assistant("first", 300, 2),
                User("run anchor", 200, 1),
                Assistant("second", 400, 3)
            ],
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, reconciliation.OutputsSafe.Select(output => output.SequenceIndex).ToArray());
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, reconciliation.OutputsSafe.Select(output => output.Text).ToArray());
    }

    [TestMethod]
    public void BoundaryReconciliation_StreamingGrowthUpdatesExistingOutputSlot()
    {
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("old", 1));
        var started = DateTimeOffset.UtcNow;
        var first = accumulator.ApplyThreadSnapshot(
            [User("run anchor", 200, 1), Assistant("draft", 300, 2)],
            started,
            Guid.NewGuid(),
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);
        var second = accumulator.ApplyThreadSnapshot(
            [User("run anchor", 200, 1), Assistant("draft with more text", 300, 2, "rerendered")],
            started.AddSeconds(1),
            first.Boundary!.TaskId,
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);

        Assert.AreEqual(1, second.OutputsSafe.Count);
        Assert.AreEqual(0, second.OutputsSafe[0].SequenceIndex);
        Assert.AreEqual("draft with more text", second.OutputsSafe[0].Text);
        Assert.AreEqual(0, accumulator.LastNewOutputCount);
        Assert.AreEqual(1, accumulator.LastChangedOutputCount);
    }

    [TestMethod]
    public void BoundaryReconciliation_IdenticalTextInTwoBlocksRemainsTwoOutputs()
    {
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("old", 1));

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                User("run anchor", 200, 0),
                Assistant("OK", 300, 1, "same"),
                Assistant("OK", 400, 2, "same")
            ],
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);

        Assert.AreEqual(2, reconciliation.OutputsSafe.Count);
        Assert.AreEqual(2, accumulator.LastNewOutputCount);
    }

    [TestMethod]
    public void BoundaryReconciliation_RuntimeIdentityChangePreservesOutputSlot()
    {
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("old", 1));
        var started = DateTimeOffset.UtcNow;
        accumulator.ApplyThreadSnapshot(
            [User("run anchor", 200, 0), Assistant("OK", 300, 1, "runtime-a")],
            started,
            Guid.NewGuid(),
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [User("run anchor", 200, 0), Assistant("OK", 300, 1, "runtime-b")],
            started.AddSeconds(1),
            accumulator.Boundary!.TaskId,
            1,
            CodexRunSemanticAnchors.FromText("run anchor"),
            isFlexColumnReverse: true);

        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
        Assert.AreEqual(0, reconciliation.OutputsSafe[0].SequenceIndex);
        Assert.AreEqual(0, accumulator.LastNewOutputCount);
    }

    [TestMethod]
    public void BoundaryReconciliation_NewUserAfterBoundarySupersedesRunWithoutCrossMixing()
    {
        var accumulator = new CodexRunOutputAccumulator(BaselineWithUser("old", 1));
        var taskId = Guid.NewGuid();
        var anchors = CodexRunSemanticAnchors.FromText("run anchor");
        accumulator.ApplyThreadSnapshot(
            [User("run anchor", 200, 0), Assistant("first", 300, 1)],
            DateTimeOffset.UtcNow,
            taskId,
            1,
            anchors,
            isFlexColumnReverse: true);

        var reconciliation = accumulator.ApplyThreadSnapshot(
            [
                User("run anchor", 200, 0),
                Assistant("first", 300, 1),
                User("manual next turn", 400, 2),
                Assistant("must not mix", 500, 3)
            ],
            DateTimeOffset.UtcNow.AddSeconds(1),
            taskId,
            1,
            anchors,
            isFlexColumnReverse: true);

        Assert.IsTrue(reconciliation.BoundarySuperseded);
        Assert.AreEqual(1, reconciliation.OutputsSafe.Count);
        Assert.AreEqual("first", reconciliation.OutputsSafe[0].Text);
    }

    [TestMethod]
    public void OrderedProjection_UsesAscendingBoundsForFlexColumnReverse()
    {
        var projection = OrderedThreadActionProjection.NormalizeThreadOrder(
            [
                Assistant("new", 600, 0),
                User("current", 500, 1),
                Assistant("old", 400, 2)
            ],
            isFlexColumnReverse: true);

        CollectionAssert.AreEqual(
            new[] { "old", "current", "new" },
            projection.ItemsSafe.Select(item => item.Text).ToArray());
        StringAssert.Contains(projection.Method, "flex_col_reverse");
    }

    private static CodexRunBaseline BaselineWithUser(
        string userText,
        int assistantCount,
        string fingerprint = "historical-assistant")
    {
        var blocks = Enumerable.Range(0, assistantCount)
            .Select(index => new CodexRunBlockBaseline(
                index,
                fingerprint,
                "historical-parent",
                5,
                CodexRunOutputAccumulator.ComputeTextPrefixHash("old-" + index)))
            .ToList();
        var user = new CodexRunUserMessageBaseline(
            0,
            fingerprint == "historical-assistant" ? "historical-user" : "user-history",
            "historical-user-parent",
            "user-action",
            userText.Length,
            CodexRunOutputAccumulator.ComputeTextPrefixHash(userText),
            CodexRunOutputAccumulator.ComputeContentSignature(userText));
        return new CodexRunBaseline(true, assistantCount, blocks, [user]);
    }

    private static CodexRunThreadActionObservation User(
        string text,
        double top,
        int sourceOrdinal,
        string fingerprint = "user",
        string actionFingerprint = "user-action") =>
        new(
            CodexRunThreadItemKind.UserMessage,
            fingerprint,
            "user-parent",
            actionFingerprint,
            "copy-message",
            text,
            new UiAutomationBounds(0, top, 100, 20),
            sourceOrdinal);

    private static CodexRunThreadActionObservation Assistant(
        string text,
        double top,
        int sourceOrdinal,
        string fingerprint = "assistant",
        string actionFingerprint = "assistant-action") =>
        new(
            CodexRunThreadItemKind.AssistantOutput,
            fingerprint,
            "assistant-parent",
            actionFingerprint,
            "copy",
            text,
            new UiAutomationBounds(0, top, 100, 20),
            sourceOrdinal);
}
