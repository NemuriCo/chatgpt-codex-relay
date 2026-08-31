using BlueRelay.Services.Desktop;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexSendCommitTests
{
    [TestMethod]
    public void A_InvokeSuccessRemainsCommittedPastTheOldTimeoutBoundary()
    {
        var result = Committed(CodexSendPostCheckStatus.Pending);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.SendCommitted);
        Assert.IsTrue(CodexSendCommitPolicy.CanAdvanceToRun(result));
        Assert.IsFalse(CodexSendCommitPolicy.IsRetryableFailure(result));
    }

    [TestMethod]
    public void B_PostcheckTimeoutCannotTurnACommittedSendIntoFailure()
    {
        var result = Committed(CodexSendPostCheckStatus.Timeout);

        Assert.IsTrue(result.SendCommitted);
        Assert.IsFalse(CodexSendCommitPolicy.IsRetryableFailure(result));
    }

    [TestMethod]
    public void C_PostcheckUnavailableCannotTurnACommittedSendIntoFailure()
    {
        var result = Committed(CodexSendPostCheckStatus.Unavailable);

        Assert.IsTrue(result.SendCommitted);
        Assert.IsFalse(CodexSendCommitPolicy.IsRetryableFailure(result));
    }

    [TestMethod]
    public void D_CancellationAfterInvokeKeepsCommittedSemantics()
    {
        var result = Committed(CodexSendPostCheckStatus.Pending);

        Assert.IsTrue(result.InvokeSucceeded);
        Assert.AreEqual(CodexSendCommitState.Committed, result.CommitState);
        Assert.IsFalse(CodexSendCommitPolicy.IsRetryableFailure(result));
    }

    [TestMethod]
    public void E_InvokeExceptionBeforeCommitRemainsRetryable()
    {
        var result = CodexComposerSendResult.Failed(
            "codex_send_invoke_failed",
            "invoke failed",
            invokeAttempted: true);

        Assert.IsTrue(result.InvokeAttempted);
        Assert.IsFalse(result.SendCommitted);
        Assert.IsTrue(CodexSendCommitPolicy.IsRetryableFailure(result));
    }

    [TestMethod]
    public void F_CancellationBeforeInvokeRemainsUncommitted()
    {
        var result = CodexComposerSendResult.Failed(
            "codex_send_cancelled",
            "cancelled before invoke");

        Assert.IsFalse(result.InvokeAttempted);
        Assert.AreEqual(CodexSendCommitState.NotCommitted, result.CommitState);
        Assert.IsTrue(CodexSendCommitPolicy.IsRetryableFailure(result));
    }

    [TestMethod]
    public void G_CommittedSendHasNoReusableFillReceipt()
    {
        var workstreamId = Guid.NewGuid();
        var receipts = new Dictionary<Guid, CodexFillReceipt>
        {
            [workstreamId] = Receipt(workstreamId)
        };
        var result = Committed(CodexSendPostCheckStatus.Pending);

        if (CodexSendCommitPolicy.ShouldClearFillReceipt(result))
        {
            receipts.Remove(workstreamId);
        }

        Assert.IsFalse(receipts.ContainsKey(workstreamId));
    }

    [TestMethod]
    public void H_ObserverReceiptUsesTheCapturedBaselineExactlyOnce()
    {
        var baseline = new CodexRunBaseline(
            true,
            1,
            [new CodexRunBlockBaseline(
                0,
                "history",
                "history-parent",
                7,
                "hash")]);
        var result = Committed(CodexSendPostCheckStatus.Pending, baseline);
        var receipt = new CodexRunReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new IntPtr(1),
            42,
            DateTimeOffset.UtcNow,
            CodexComposerInjectionMode.ValuePatternVerified,
            result.RunBaseline!);

        Assert.AreSame(baseline, receipt.PreSendBaseline);
        Assert.AreEqual(1, receipt.PreSendBaseline.AssistantBlockCount);
    }

    [TestMethod]
    public void I_PendingPostcheckIsNotRequiredToAdvanceACommittedSend()
    {
        var result = Committed(CodexSendPostCheckStatus.Pending);

        Assert.IsNull(result.PostCheck);
        Assert.IsTrue(result.SendCommitted);
        Assert.IsTrue(CodexSendCommitPolicy.CanAdvanceToRun(result));
    }

    [TestMethod]
    public void J_SmartSendButtonSelectionStillRejectsAmbiguousCandidates()
    {
        var send = SendButton("Send message", "send-button", "CodexSendButton");
        var upload = SendButton("Upload file", "upload-button", "CodexUploadButton");

        Assert.IsTrue(CodexSendButtonSelector.TrySelect([send, upload], out var selected));
        Assert.AreSame(send, selected);
        Assert.IsFalse(CodexSendButtonSelector.TrySelect(
            [send, send with { AutomationId = "another-send-button" }],
            out _));
    }

    private static CodexComposerSendResult Committed(
        CodexSendPostCheckStatus postCheckStatus,
        CodexRunBaseline? baseline = null) =>
        new(
            true,
            "codex_send_invoked",
            "sent",
            InvokeAttempted: true,
            InvokeSucceeded: true,
            RunBaseline: baseline ?? CodexRunBaseline.Unavailable,
            CommitState: CodexSendCommitState.Committed,
            PostCheckStatus: postCheckStatus);

    private static CodexFillReceipt Receipt(Guid workstreamId) =>
        new(
            Guid.NewGuid(),
            workstreamId,
            Guid.NewGuid(),
            1,
            new IntPtr(1),
            42,
            CodexComposerInjectionMode.ValuePatternVerified,
            DateTimeOffset.UtcNow);

    private static CodexSendButtonMetadata SendButton(
        string name,
        string automationId,
        string className) =>
        new(
            "Button",
            "button",
            automationId,
            className,
            "Chrome",
            IsEnabled: true,
            IsOffscreen: false,
            InvokePatternAvailable: true,
            LegacyIAccessiblePatternAvailable: false,
            new UiAutomationBounds(10, 700, 80, 32),
            name);
}
