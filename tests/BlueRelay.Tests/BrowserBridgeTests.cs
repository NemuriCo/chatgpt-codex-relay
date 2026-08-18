using System.Net;
using System.Net.Http.Json;
using BlueRelay.Localization;
using BlueRelay.Models;
using BlueRelay.Persistence;
using BlueRelay.Presentation.ViewModels;
using BlueRelay.Services;
using BlueRelay.Services.Bridges;
using BlueRelay.Services.Codex;

namespace BlueRelay.Tests;

[TestClass]
public sealed class BrowserBridgeTests
{
    private string _testDirectory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "BlueRelayBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PairingRejectsInvalidCodeAndConsumedCode()
    {
        var (bridge, _) = CreateBridge();
        var code = bridge.GeneratePairingCode().Code!;

        var invalid = await bridge.PairAsync("000000", "installation-a");
        Assert.IsFalse(invalid.Success);
        Assert.AreEqual("pairing_invalid", invalid.ErrorCode);

        var paired = await bridge.PairAsync(code, "installation-a");
        Assert.IsTrue(paired.Success, paired.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(paired.Value!.Token));
        Assert.IsTrue(bridge.IsAuthorized(paired.Value.Token));

        var consumed = await bridge.PairAsync(code, "installation-b");
        Assert.IsFalse(consumed.Success);
        Assert.AreEqual("pairing_expired", consumed.ErrorCode);
    }

    [TestMethod]
    public async Task ConversationNavigationPausesCaptureUntilExplicitRebind()
    {
        var state = new ApplicationState();
        var projectService = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, projectService);
        var project = (await projectService.TryCreateAsync("Conversation binding", _testDirectory)).Project!;
        var workstream = project.Workstreams[0];
        Assert.IsTrue((await bridge.PairAsync(bridge.GeneratePairingCode().Code!, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        var heartbeat = await bridge.HeartbeatAsync(
            "installation-a",
            "tab-a",
            "https://chatgpt.com/c/conversation-b",
            "conversation-b",
            "Conversation B");
        Assert.IsTrue(heartbeat.Success, heartbeat.Error);
        Assert.IsTrue(bridge.FindBindingDto(workstream.Id)!.ConversationMismatch);

        var blocked = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nBlocked", "https://chatgpt.com/c/conversation-b", "conversation-b", "Conversation B"));
        Assert.IsFalse(blocked.Success);
        Assert.AreEqual("conversation_mismatch", blocked.ErrorCode);

        var implicitBind = await bridge.BindTabAsync(new BindTabRequest("installation-a", "tab-a", workstream.Id));
        Assert.IsFalse(implicitBind.Success);
        Assert.AreEqual("conversation_mismatch", implicitBind.ErrorCode);

        var explicitRebind = await bridge.BindTabAsync(new BindTabRequest("installation-a", "tab-a", workstream.Id, Rebind: true));
        Assert.IsTrue(explicitRebind.Success, explicitRebind.Error);
        Assert.AreEqual("conversation-b", workstream.ChatGPTConversationId);
        Assert.IsFalse(bridge.FindBindingDto(workstream.Id)!.ConversationMismatch);

        var captured = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nAllowed", "https://chatgpt.com/c/conversation-b", "conversation-b", "Conversation B"));
        Assert.IsTrue(captured.Success, captured.Error);
    }

    [TestMethod]
    public async Task IdenticalCaptureIsDedupedWithoutChangingPreparedOrLaterState()
    {
        var (bridge, projectService) = CreateBridge();
        var project = (await projectService.TryCreateAsync("Capture dedupe", CreateDirectory("capture-dedupe"))).Project!;
        var workstream = project.Workstreams[0];
        Assert.IsTrue((await bridge.PairAsync(bridge.GeneratePairingCode().Code!, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        var first = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\r\nA\r\n", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));
        Assert.IsTrue(first.Success, first.Error);
        var firstTask = first.Value!;
        var firstCapturedAt = firstTask.CapturedAt;
        var changedCount = 0;
        bridge.Changed += (_, _) => changedCount++;

        workstream.CurrentState = WorkflowState.ChatGPTReviewing;
        workstream.CodexProgress = "old transient status";
        var duplicate = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "  # CODEX_TASK\nA  ", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));

        Assert.IsTrue(duplicate.Success, duplicate.Error);
        Assert.AreSame(firstTask, duplicate.Value);
        Assert.AreEqual(firstTask.Id, workstream.CurrentTaskId is { } currentId ? Guid.Parse(currentId) : Guid.Empty);
        Assert.AreEqual(firstCapturedAt, duplicate.Value!.CapturedAt);
        Assert.AreEqual(WorkflowState.ChatGPTReviewing, workstream.CurrentState);
        Assert.AreEqual("old transient status", workstream.CodexProgress);
        Assert.AreEqual(0, changedCount);
    }

    [TestMethod]
    public async Task DifferentCaptureReplacesCurrentTaskResetsTransientStateAndPreservesBinding()
    {
        var state = new ApplicationState();
        var projectService = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var codex = new FakeCodexBridge(new CodexTurnResult(true, "unused", null, "unused"));
        var bridge = new BrowserBridgeService(state, projectService, codex);
        var project = (await projectService.TryCreateAsync("Capture replacement", CreateDirectory("capture-replacement"))).Project!;
        var workstream = project.Workstreams[0];
        Assert.IsTrue((await bridge.PairAsync(bridge.GeneratePairingCode().Code!, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        var first = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nA", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));
        Assert.IsTrue(first.Success, first.Error);
        first.Value!.UserNote = "old note";
        first.Value.Result = "old result";
        workstream.CodexProgress = "old progress";
        workstream.CodexError = "old error";
        workstream.CodexErrorCode = "old_error";
        var bindingBefore = bridge.FindBindingDto(workstream.Id)!;

        var second = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nB", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));

        Assert.IsTrue(second.Success, second.Error);
        Assert.AreNotEqual(first.Value.Id, second.Value!.Id);
        Assert.AreSame(second.Value, bridge.FindCurrentTask(workstream.Id));
        Assert.AreEqual("# CODEX_TASK\nB", second.Value.Prompt);
        Assert.IsNull(second.Value.UserNote);
        Assert.IsNull(second.Value.Result);
        Assert.IsNull(second.Value.ResultPayload);
        Assert.AreEqual(WorkflowState.ReadyForCodex, workstream.CurrentState);
        Assert.AreEqual(second.Value.Id.ToString("D"), workstream.CurrentTaskId);
        Assert.IsNull(workstream.CodexProgress);
        Assert.IsNull(workstream.CodexError);
        Assert.IsNull(workstream.CodexErrorCode);
        Assert.AreEqual("old result", first.Value.Result);
        Assert.AreEqual(bindingBefore, bridge.FindBindingDto(workstream.Id));
        Assert.AreEqual(0, codex.SubmitCount);

        var repeated = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nB", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));
        Assert.IsTrue(repeated.Success, repeated.Error);
        Assert.AreSame(second.Value, repeated.Value);
        Assert.AreEqual(2, state.BrowserBridge.Tasks.Count);
    }

    [TestMethod]
    public async Task ReplacementPersistsTheNewPayloadAsTheCurrentTask()
    {
        var statePath = Path.Combine(_testDirectory, "state.json");
        var payloadRoot = Path.Combine(_testDirectory, "relay");
        var state = new ApplicationState();
        var stateStore = new JsonStateStore(statePath);
        var projectService = new ProjectService(state, stateStore, new WorkflowStateMachine());
        var payloadStore = new RelayPayloadStore(payloadRoot);
        var bridge = new BrowserBridgeService(state, projectService, payloadStore: payloadStore);
        var project = (await projectService.TryCreateAsync("Payload replacement", CreateDirectory("payload-replacement"))).Project!;
        var workstream = project.Workstreams[0];
        Assert.IsTrue((await bridge.PairAsync(bridge.GeneratePairingCode().Code!, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        _ = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nA", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));
        var second = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nB", "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));
        Assert.IsTrue(second.Success, second.Error);

        var reloaded = await stateStore.LoadAsync();
        var reloadedWorkstream = reloaded.State.Projects.Single().Workstreams.Single();
        var reloadedTask = reloaded.State.BrowserBridge.Tasks.Single(task => task.Id == second.Value!.Id);
        Assert.AreEqual(second.Value!.Id.ToString("D"), reloadedWorkstream.CurrentTaskId);
        Assert.AreEqual("# CODEX_TASK\nB", payloadStore.Read(reloadedTask.Payload));
        Assert.IsNull(reloadedTask.Result);
    }

    [TestMethod]
    public async Task BrowserCaptureAndTaskPayloadPreserveFiveSixteenAndThirtyTwoKilobyteTasks()
    {
        var state = new ApplicationState();
        var payloadStore = new RelayPayloadStore(Path.Combine(_testDirectory, "long-payloads"));
        var projectService = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, projectService, payloadStore: payloadStore);
        var project = (await projectService.TryCreateAsync("Long payloads", CreateDirectory("long-payloads"))).Project!;
        var workstream = project.Workstreams[0];
        Assert.IsTrue((await bridge.PairAsync(bridge.GeneratePairingCode().Code!, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        foreach (var size in new[] { 5 * 1024, 16 * 1024, 32 * 1024 })
        {
            var payload = BuildLongPayload(size);
            var capture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
                "installation-a", "tab-a", payload, "https://chatgpt.com/c/tab-a", "tab-a", "tab-a"));

            Assert.IsTrue(capture.Success, capture.Error);
            Assert.AreEqual(payload, capture.Value!.Prompt);
            Assert.AreEqual(payload, payloadStore.Read(capture.Value.Payload));
            Assert.AreEqual(payload.Length, capture.Value.Prompt.Length);
        }
    }

    [TestMethod]
    public async Task StaleTabCanBeReplacedByAnotherTabForTheSameConversation()
    {
        var state = new ApplicationState();
        var projectService = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, projectService);
        var project = (await projectService.TryCreateAsync("Browser restart", _testDirectory)).Project!;
        var workstream = project.Workstreams[0];
        Assert.IsTrue((await bridge.PairAsync(bridge.GeneratePairingCode().Code!, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "old-tab", workstream.Id);
        var oldBinding = state.BrowserBridge.Bindings.Single(item => item.TabId == "old-tab");
        oldBinding.Connected = false;
        oldBinding.LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-2);

        var register = await bridge.RegisterTabAsync(new RegisterTabRequest(
            "installation-a", "new-tab", "https://chatgpt.com/c/new", "old-tab", "New browser tab"));
        Assert.IsTrue(register.Success, register.Error);
        var bind = await bridge.BindTabAsync(new BindTabRequest("installation-a", "new-tab", workstream.Id));

        Assert.IsTrue(bind.Success, bind.Error);
        Assert.AreEqual("new-tab", workstream.ChatGPTTabId);
        Assert.AreEqual("old-tab", workstream.ChatGPTConversationId);
        Assert.IsNull(oldBinding.WorkstreamId);
        Assert.AreEqual(workstream.Id, bridge.FindBindingDto(workstream.Id)!.WorkstreamId);
    }

    [TestMethod]
    public async Task TwoTabsCaptureAndHandoffOnlyTheirOwnWorkstreams()
    {
        var (bridge, projectService) = CreateBridge();
        var first = (await projectService.TryCreateAsync("First", CreateDirectory("first"))).Project!;
        var second = (await projectService.TryCreateAsync("Second", CreateDirectory("second"))).Project!;
        var code = bridge.GeneratePairingCode().Code!;
        Assert.IsTrue((await bridge.PairAsync(code, "installation-a")).Success);

        await RegisterAndBindAsync(bridge, "tab-a", first.Workstreams[0].Id);
        await RegisterAndBindAsync(bridge, "tab-b", second.Workstreams[0].Id);

        var listing = bridge.ListWorkstreams();
        Assert.AreEqual(2, listing.Count);
        Assert.AreEqual("installation-a:tab-a", listing.Single(item => item.WorkstreamId == first.Workstreams[0].Id).Binding!.TabKey);

        var ignored = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-b", "just a normal copied URL", "https://chatgpt.com/c/tab-b", "tab-b", "Second tab"));
        Assert.IsFalse(ignored.Success);
        Assert.AreEqual("not_a_codex_task", ignored.ErrorCode);

        var firstCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nImplement First", "https://chatgpt.com/c/first", "tab-a", "First tab"));
        Assert.IsTrue(firstCapture.Success, firstCapture.Error);
        Assert.AreEqual(WorkflowState.ReadyForCodex, first.Workstreams[0].CurrentState);
        Assert.AreEqual(WorkflowState.Idle, second.Workstreams[0].CurrentState);
        Assert.AreEqual(first.Workstreams[0].Id, firstCapture.Value!.WorkstreamId);

        Assert.IsTrue((await bridge.ConfirmTaskAsync(firstCapture.Value.Id)).Success);
        var result = await bridge.SimulateResultAsync(firstCapture.Value.Id, "simulated result");
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, first.Workstreams[0].CurrentState);

        var handoff = await bridge.QueueHandoffAsync(firstCapture.Value.Id);
        Assert.IsTrue(handoff.Success, handoff.Error);
        Assert.AreEqual("tab-a", handoff.Value!.TabId);
        Assert.AreEqual(first.Workstreams[0].Id, handoff.Value.WorkstreamId);
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, first.Workstreams[0].CurrentState);
        Assert.AreEqual(RelayCommandDeliveryStatus.Queued, firstCapture.Value.DeliveryStatus);

        var tabBCommand = await bridge.GetNextCommandAsync("installation-a", "tab-b");
        Assert.IsTrue(tabBCommand.Success, tabBCommand.Error);
        Assert.IsNull(tabBCommand.Value);

        var delivery = await bridge.GetNextCommandAsync("installation-a", "tab-a");
        Assert.IsTrue(delivery.Success, delivery.Error);
        Assert.AreEqual(handoff.Value.CommandId, delivery.Value!.CommandId);
        Assert.AreEqual(RelayCommandDeliveryStatus.Delivering, firstCapture.Value.DeliveryStatus);
        var duplicateDelivery = await bridge.GetNextCommandAsync("installation-a", "tab-a");
        Assert.IsTrue(duplicateDelivery.Success, duplicateDelivery.Error);
        Assert.IsNull(duplicateDelivery.Value);

        var failedAck = await bridge.AcknowledgeHandoffAsync(delivery.Value.CommandId, false, "composer_not_found");
        Assert.IsTrue(failedAck.Success, failedAck.Error);
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, first.Workstreams[0].CurrentState);
        Assert.AreEqual(RelayCommandDeliveryStatus.Failed, firstCapture.Value.DeliveryStatus);
        Assert.AreEqual("composer_not_found", firstCapture.Value.DeliveryErrorCode);
        Assert.AreEqual("simulated result", firstCapture.Value.Result);

        var retry = await bridge.QueueHandoffAsync(firstCapture.Value.Id);
        Assert.IsTrue(retry.Success, retry.Error);
        Assert.AreEqual(RelayCommandDeliveryStatus.Queued, firstCapture.Value.DeliveryStatus);
        var retryDelivery = await bridge.GetNextCommandAsync("installation-a", "tab-a");
        Assert.IsTrue(retryDelivery.Success, retryDelivery.Error);
        Assert.AreEqual(retry.Value!.CommandId, retryDelivery.Value!.CommandId);
        Assert.IsTrue((await bridge.AcknowledgeHandoffAsync(retryDelivery.Value.CommandId, true, null, CancellationToken.None)).Success);
        Assert.AreEqual(WorkflowState.ChatGPTReviewing, first.Workstreams[0].CurrentState);
        Assert.AreEqual(RelayCommandDeliveryStatus.Delivered, firstCapture.Value.DeliveryStatus);
        Assert.AreEqual(WorkflowState.Idle, second.Workstreams[0].CurrentState);
    }

    [TestMethod]
    public async Task CompletingRoundPreservesTaskAndAllowsCompletedAndReviewingNextRounds()
    {
        var (bridge, projectService) = CreateBridge();
        var project = (await projectService.TryCreateAsync("Round lifecycle", CreateDirectory("round-lifecycle"))).Project!;
        var workstream = project.Workstreams[0];
        var code = bridge.GeneratePairingCode().Code!;
        Assert.IsTrue((await bridge.PairAsync(code, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        var firstCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nFirst round", "https://chatgpt.com/c/first", "tab-a", "First tab"));
        Assert.IsTrue(firstCapture.Success, firstCapture.Error);
        Assert.IsTrue((await bridge.ConfirmTaskAsync(firstCapture.Value!.Id)).Success);
        Assert.IsTrue((await bridge.SimulateResultAsync(firstCapture.Value.Id, "first result")).Success);
        var queued = await bridge.QueueHandoffAsync(firstCapture.Value.Id);
        Assert.IsTrue(queued.Success, queued.Error);

        var completed = await bridge.CompleteTaskAsync(firstCapture.Value.Id);
        Assert.IsTrue(completed.Success, completed.Error);
        Assert.AreEqual(WorkflowState.Completed, workstream.CurrentState);
        Assert.AreEqual(RelayTaskStatus.Completed, firstCapture.Value.Status);
        Assert.AreEqual("# CODEX_TASK\nFirst round", firstCapture.Value.Prompt);
        Assert.AreEqual("first result", firstCapture.Value.Result);
        Assert.AreEqual(RelayCommandDeliveryStatus.None, firstCapture.Value.DeliveryStatus);
        Assert.AreEqual(firstCapture.Value.Id.ToString("D"), workstream.CurrentTaskId);
        Assert.AreEqual(workstream.Id, bridge.FindBindingDto(workstream.Id)!.WorkstreamId);
        Assert.IsNull((await bridge.GetNextCommandAsync("installation-a", "tab-a")).Value);

        var secondCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nSecond round", "https://chatgpt.com/c/second", "tab-a", "First tab"));
        Assert.IsTrue(secondCapture.Success, secondCapture.Error);
        Assert.AreNotEqual(firstCapture.Value.Id, secondCapture.Value!.Id);
        Assert.AreEqual(WorkflowState.ReadyForCodex, workstream.CurrentState);
        Assert.AreEqual(secondCapture.Value.Id.ToString("D"), workstream.CurrentTaskId);
        Assert.IsNull(secondCapture.Value.Result);

        Assert.IsTrue((await bridge.ConfirmTaskAsync(secondCapture.Value.Id)).Success);
        Assert.IsTrue((await bridge.SimulateResultAsync(secondCapture.Value.Id, "second result")).Success);
        var secondHandoff = await bridge.QueueHandoffAsync(secondCapture.Value.Id);
        Assert.IsTrue(secondHandoff.Success, secondHandoff.Error);
        var secondDelivery = await bridge.GetNextCommandAsync("installation-a", "tab-a");
        Assert.IsTrue(secondDelivery.Success, secondDelivery.Error);
        Assert.IsTrue((await bridge.AcknowledgeHandoffAsync(secondDelivery.Value!.CommandId, true)).Success);
        Assert.AreEqual(WorkflowState.ChatGPTReviewing, workstream.CurrentState);

        var thirdCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nReview follow-up", "https://chatgpt.com/c/follow-up", "tab-a", "First tab"));
        Assert.IsTrue(thirdCapture.Success, thirdCapture.Error);
        Assert.AreEqual(WorkflowState.ReadyForCodex, workstream.CurrentState);
        Assert.AreEqual(thirdCapture.Value!.Id.ToString("D"), workstream.CurrentTaskId);
        Assert.IsNull(thirdCapture.Value.Result);
        Assert.AreEqual("second result", secondCapture.Value.Result);
    }

    [TestMethod]
    public async Task ClearingReviewingTaskReturnsToIdleKeepsBindingAndDoesNotAffectSibling()
    {
        var (bridge, projectService) = CreateBridge();
        var project = (await projectService.TryCreateAsync("Clear lifecycle", CreateDirectory("clear-lifecycle"))).Project!;
        var firstWorkstream = project.Workstreams[0];
        var secondWorkstream = (await projectService.TryCreateWorkstreamAsync(project.Id, "Sibling")).Workstream!;
        var code = bridge.GeneratePairingCode().Code!;
        Assert.IsTrue((await bridge.PairAsync(code, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", firstWorkstream.Id);
        await RegisterAndBindAsync(bridge, "tab-b", secondWorkstream.Id);

        var firstCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nReview me", "https://chatgpt.com/c/first", "tab-a", "First tab"));
        Assert.IsTrue(firstCapture.Success, firstCapture.Error);
        Assert.IsTrue((await bridge.ConfirmTaskAsync(firstCapture.Value!.Id)).Success);
        Assert.IsTrue((await bridge.SimulateResultAsync(firstCapture.Value.Id, "review result")).Success);
        var handoff = await bridge.QueueHandoffAsync(firstCapture.Value.Id);
        Assert.IsTrue(handoff.Success, handoff.Error);
        var delivery = await bridge.GetNextCommandAsync("installation-a", "tab-a");
        Assert.IsTrue(delivery.Success, delivery.Error);
        Assert.IsTrue((await bridge.AcknowledgeHandoffAsync(delivery.Value!.CommandId, true)).Success);
        Assert.AreEqual(WorkflowState.ChatGPTReviewing, firstWorkstream.CurrentState);

        var siblingCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-b", "# CODEX_TASK\nSibling task", "https://chatgpt.com/c/sibling", "tab-b", "Sibling tab"));
        Assert.IsTrue(siblingCapture.Success, siblingCapture.Error);

        var clear = await bridge.ClearCurrentTaskAsync(firstWorkstream.Id);
        Assert.IsTrue(clear.Success, clear.Error);
        Assert.AreEqual(WorkflowState.Idle, firstWorkstream.CurrentState);
        Assert.IsNull(firstWorkstream.CurrentTaskId);
        Assert.IsNull(bridge.FindCurrentTask(firstWorkstream.Id));
        Assert.AreEqual("review result", firstCapture.Value.Result);
        Assert.AreEqual(RelayCommandDeliveryStatus.None, firstCapture.Value.DeliveryStatus);
        Assert.AreEqual(firstWorkstream.Id, bridge.FindBindingDto(firstWorkstream.Id)!.WorkstreamId);
        Assert.AreEqual(WorkflowState.ReadyForCodex, secondWorkstream.CurrentState);
        Assert.AreEqual(siblingCapture.Value!.Id.ToString("D"), secondWorkstream.CurrentTaskId);

        var newCapture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nFresh task", "https://chatgpt.com/c/fresh", "tab-a", "First tab"));
        Assert.IsTrue(newCapture.Success, newCapture.Error);
        Assert.AreEqual(WorkflowState.ReadyForCodex, firstWorkstream.CurrentState);
        Assert.AreEqual(newCapture.Value!.Id.ToString("D"), firstWorkstream.CurrentTaskId);
        Assert.IsNull(newCapture.Value.Result);
        Assert.AreEqual(WorkflowState.ReadyForCodex, secondWorkstream.CurrentState);
    }

    [TestMethod]
    public async Task ClearingCurrentTaskCancelsPendingDeliveryCommand()
    {
        var (bridge, projectService) = CreateBridge();
        var project = (await projectService.TryCreateAsync("Clear pending", CreateDirectory("clear-pending"))).Project!;
        var workstream = project.Workstreams[0];
        var code = bridge.GeneratePairingCode().Code!;
        Assert.IsTrue((await bridge.PairAsync(code, "installation-a")).Success);
        await RegisterAndBindAsync(bridge, "tab-a", workstream.Id);

        var capture = await bridge.CaptureTaskAsync(new CaptureTaskRequest(
            "installation-a", "tab-a", "# CODEX_TASK\nPending task", "https://chatgpt.com/c/pending", "tab-a", "First tab"));
        Assert.IsTrue(capture.Success, capture.Error);
        Assert.IsTrue((await bridge.ConfirmTaskAsync(capture.Value!.Id)).Success);
        Assert.IsTrue((await bridge.SimulateResultAsync(capture.Value.Id, "pending result")).Success);
        Assert.IsTrue((await bridge.QueueHandoffAsync(capture.Value.Id)).Success);
        var delivery = await bridge.GetNextCommandAsync("installation-a", "tab-a");
        Assert.IsTrue(delivery.Success, delivery.Error);
        Assert.AreEqual(RelayCommandDeliveryStatus.Delivering, capture.Value.DeliveryStatus);

        var clear = await bridge.ClearCurrentTaskAsync(workstream.Id);
        Assert.IsTrue(clear.Success, clear.Error);
        Assert.IsNull((await bridge.GetNextCommandAsync("installation-a", "tab-a")).Value);
        Assert.AreEqual(WorkflowState.Idle, workstream.CurrentState);
        Assert.IsNull(workstream.CurrentTaskId);
        Assert.AreEqual(RelayCommandDeliveryStatus.None, capture.Value.DeliveryStatus);
        Assert.IsNull(capture.Value.DeliveryErrorCode);
    }

    [TestMethod]
    public async Task UnauthenticatedBridgeRequestsAreRejectedAndServerStops()
    {
        var (bridge, _) = CreateBridge();
        var port = GetUnusedPort();
        await using var server = new BrowserBridgeServer(bridge, port);
        var start = await server.StartAsync();
        Assert.IsTrue(start.Success, start.Error);

        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/v1/workstreams");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        await server.StopAsync();
        Assert.IsFalse(server.IsRunning);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/v1/health/no-longer-running"));
    }

    [TestMethod]
    public async Task CodexThreadFailureLeavesWorkstreamOutOfRunning()
    {
        var state = new ApplicationState();
        var projectService = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var codex = new FakeCodexBridge(new CodexTurnResult(
            false,
            "thread-archived",
            null,
            null,
            "Codex thread is archived.",
            ErrorCode: "codex_thread_archived"));
        var bridge = new BrowserBridgeService(state, projectService, codex);
        var project = (await projectService.TryCreateAsync("Codex failure", _testDirectory)).Project!;
        var workstream = project.Workstreams[0];
        workstream.CurrentState = WorkflowState.ReadyForCodex;
        workstream.CurrentTaskId = Guid.NewGuid().ToString("D");
        var task = new RelayTask
        {
            Id = Guid.Parse(workstream.CurrentTaskId),
            WorkstreamId = workstream.Id,
            Prompt = "resume archived thread",
            Status = RelayTaskStatus.Captured
        };
        state.BrowserBridge.Tasks.Add(task);

        var result = await bridge.SendTaskToCodexAsync(task.Id);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("codex_thread_archived", result.ErrorCode);
        Assert.AreEqual(WorkflowState.NeedsAttention, workstream.CurrentState);
        Assert.AreNotEqual(WorkflowState.CodexRunning, workstream.CurrentState);
        Assert.AreEqual(CodexBridgeStatus.Connected, codex.Status);
        Assert.AreEqual(RelayTaskStatus.Error, task.Status);
        Assert.AreEqual("codex_thread_archived", workstream.CodexErrorCode);
    }

    [TestMethod]
    public async Task NewCodexSessionClearsPersistedBindingKeepsTaskAndRefreshesUi()
    {
        var statePath = Path.Combine(_testDirectory, "state.json");
        var state = new ApplicationState();
        var store = new JsonStateStore(statePath);
        var projectService = new ProjectService(state, store, new WorkflowStateMachine());
        var codex = new FakeCodexBridge(new CodexTurnResult(true, "new-thread", null, "result"));
        var bridge = new BrowserBridgeService(state, projectService, codex);
        var project = (await projectService.TryCreateAsync("Reset binding", _testDirectory)).Project!;
        var workstream = project.Workstreams[0];
        workstream.CodexThreadId = "old-thread";
        workstream.CodexSessionId = "old-thread";
        workstream.CodexError = "old thread failed";
        workstream.CodexErrorCode = "codex_thread_conflict";
        workstream.CurrentState = WorkflowState.NeedsAttention;
        var task = new RelayTask
        {
            WorkstreamId = workstream.Id,
            Prompt = "# CODEX_TASK\nKeep me",
            UserNote = "Keep this note"
        };
        state.BrowserBridge.Tasks.Add(task);
        workstream.CurrentTaskId = task.Id.ToString("D");
        await projectService.TrySaveAsync();

        var item = new ProjectListItemViewModel(
            project,
            workstream,
            LocalizationService.ForCulture(new System.Globalization.CultureInfo("zh-CN")),
            bridge);
        var changed = false;
        bridge.Changed += (_, _) =>
        {
            changed = true;
            item.Refresh();
        };

        var result = await bridge.ResetCodexThreadAsync(workstream.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(changed);
        Assert.IsNull(workstream.CodexThreadId);
        Assert.IsNull(workstream.CodexSessionId);
        Assert.IsNull(workstream.CodexError);
        Assert.IsNull(workstream.CodexErrorCode);
        Assert.AreEqual(task.Id.ToString("D"), workstream.CurrentTaskId);
        Assert.AreEqual(task, bridge.FindCurrentTask(workstream.Id));
        Assert.AreEqual("未绑定 Codex 会话", item.CodexPairingText);

        var reloaded = await store.LoadAsync();
        var reloadedWorkstream = reloaded.State.Projects.Single().Workstreams.Single();
        Assert.IsNull(reloadedWorkstream.CodexThreadId);
        Assert.IsNull(reloadedWorkstream.CodexSessionId);
        Assert.AreEqual(task.Id.ToString("D"), reloadedWorkstream.CurrentTaskId);
        Assert.AreEqual("Keep this note", reloaded.State.BrowserBridge.Tasks.Single().UserNote);
    }

    private static async Task RegisterAndBindAsync(BrowserBridgeService bridge, string tabId, Guid workstreamId)
    {
        var register = await bridge.RegisterTabAsync(new RegisterTabRequest(
            "installation-a", tabId, $"https://chatgpt.com/c/{tabId}", tabId, tabId));
        Assert.IsTrue(register.Success, register.Error);
        var bind = await bridge.BindTabAsync(new BindTabRequest("installation-a", tabId, workstreamId));
        Assert.IsTrue(bind.Success, bind.Error);
    }

    private static (BrowserBridgeService Bridge, ProjectService ProjectService) CreateBridge()
    {
        var state = new ApplicationState();
        var service = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        return (new BrowserBridgeService(state, service), service);
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_testDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string BuildLongPayload(int minimumLength)
    {
        const string prefix = "# CODEX_TASK\nBLUERELAY_PAYLOAD_START_7F3A\n中文 English Markdown C# JSON PowerShell C:\\Projects\\BlueRelay 😊\n";
        const string repeated = "重复长段落：**bold** `inline` {\"key\":\"值\"} arrow → infinity ∞\n";
        const string suffix = "BLUERELAY_PAYLOAD_END_91C2";
        var builder = new System.Text.StringBuilder(prefix);
        while (builder.Length + suffix.Length + repeated.Length < minimumLength)
        {
            builder.Append(repeated);
        }

        builder.Append(suffix);
        return builder.ToString();
    }

    private static int GetUnusedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class MemoryStateStore : IStateStore
    {
        public string FilePath => "memory://state";

        public Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StateLoadResult(new ApplicationState(), null));

        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCodexBridge : ICodexBridge
    {
        private readonly CodexTurnResult _result;

        public FakeCodexBridge(CodexTurnResult result)
        {
            _result = result;
            _ = ProgressChanged;
            _ = ApprovalRequested;
            _ = ThreadChanged;
            _ = StatusChanged;
            _ = DiagnosticsChanged;
        }

        public CodexBridgeStatus Status => CodexBridgeStatus.Connected;

        public string? Version => "fake";

        public string? ErrorMessage => null;

        public CodexDiagnosticSnapshot Diagnostics => new(null, Version, null, null, "test", null, []);

        public int SubmitCount { get; private set; }

        public event EventHandler<CodexProgressUpdate>? ProgressChanged;

        public event EventHandler<CodexApprovalRequest>? ApprovalRequested;

        public event EventHandler<CodexThreadUpdate>? ThreadChanged;

        public event EventHandler? StatusChanged;

        public event EventHandler? DiagnosticsChanged;

        public Task<CodexTurnResult> SubmitTaskAsync(CodexTaskRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(_result);
        }

        public Task<bool> InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task RespondToApprovalAsync(
            string requestId,
            string method,
            bool approved,
            System.Text.Json.JsonElement? parameters = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetThreadAsync(Workstream workstream, CancellationToken cancellationToken = default)
        {
            workstream.CodexThreadId = null;
            workstream.CodexSessionId = null;
            workstream.CodexProgress = null;
            workstream.CodexError = null;
            workstream.CodexErrorCode = null;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
