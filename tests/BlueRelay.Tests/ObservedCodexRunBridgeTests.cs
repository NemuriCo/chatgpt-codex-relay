using BlueRelay.Models;
using BlueRelay.Persistence;
using BlueRelay.Services;
using BlueRelay.Services.Bridges;
using BlueRelay.Services.Desktop;

namespace BlueRelay.Tests;

[TestClass]
public sealed class ObservedCodexRunBridgeTests
{
    [TestMethod]
    public async Task CompleteObservedRunWritesAggregateAndTransitionsToReadyForChatGpt()
    {
        var state = new ApplicationState();
        var service = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, service);
        var project = (await service.TryCreateAsync("Observed run", Path.GetTempPath())).Project!;
        var workstream = project.Workstreams[0];
        var task = new RelayTask
        {
            WorkstreamId = workstream.Id,
            Prompt = "# CODEX_TASK\nObserve this",
            Status = RelayTaskStatus.CodexRunning
        };
        state.BrowserBridge.Tasks.Add(task);
        workstream.CurrentTaskId = task.Id.ToString("D");
        workstream.CurrentState = WorkflowState.CodexRunning;

        var receipt = Receipt(project.Id, workstream.Id, task.Id, generation: 3);
        var now = DateTimeOffset.UtcNow;
        var result = new CodexRunResult(
            receipt.RunId,
            receipt.ProjectId,
            receipt.WorkstreamId,
            receipt.TaskId,
            receipt.Generation,
            receipt.WindowHandle,
            receipt.ProcessId,
            receipt.StartedAtUtc,
            now,
            CodexRunCompletionMode.NativeRunControl,
            [
                Output(1, "second output"),
                Output(0, "first output")
            ],
            true,
            "codex_run_completed");

        var completed = await bridge.CompleteObservedCodexRunAsync(receipt, result);

        Assert.IsTrue(completed.Success, completed.Error);
        Assert.AreSame(task, completed.Value);
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, workstream.CurrentState);
        Assert.AreEqual(RelayTaskStatus.ReadyForChatGPT, task.Status);
        Assert.AreEqual("[Codex Output 1/2]\r\nfirst output\r\n---\r\n\r\n[Codex Output 2/2]\r\nsecond output", task.Result);
        Assert.AreEqual(2, task.CodexRunOutputCount);
        Assert.AreEqual(receipt.RunId, task.CodexRunId);
        Assert.IsNotNull(task.ResultPayload);
    }

    [TestMethod]
    public async Task StaleObservedRunCannotWriteResult()
    {
        var state = new ApplicationState();
        var service = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, service);
        var project = (await service.TryCreateAsync("Stale observed run", Path.GetTempPath())).Project!;
        var workstream = project.Workstreams[0];
        var task = new RelayTask
        {
            WorkstreamId = workstream.Id,
            Prompt = "# CODEX_TASK\nStale",
            Status = RelayTaskStatus.CodexRunning
        };
        state.BrowserBridge.Tasks.Add(task);
        workstream.CurrentTaskId = task.Id.ToString("D");
        workstream.CurrentState = WorkflowState.CodexRunning;

        var receipt = Receipt(project.Id, workstream.Id, task.Id, generation: 1);
        var result = new CodexRunResult(
            receipt.RunId,
            receipt.ProjectId,
            receipt.WorkstreamId,
            receipt.TaskId,
            receipt.Generation + 1,
            receipt.WindowHandle,
            receipt.ProcessId,
            receipt.StartedAtUtc,
            DateTimeOffset.UtcNow,
            CodexRunCompletionMode.NativeRunControl,
            [Output(0, "must not be written")],
            true,
            "codex_run_completed");

        var completed = await bridge.CompleteObservedCodexRunAsync(receipt, result);

        Assert.IsFalse(completed.Success);
        Assert.AreEqual("codex_run_stale", completed.ErrorCode);
        Assert.IsNull(task.Result);
        Assert.AreEqual(WorkflowState.CodexRunning, workstream.CurrentState);
    }

    [TestMethod]
    public async Task PartialObservedRunIsRetainedWithoutTransitioningToReady()
    {
        var state = new ApplicationState();
        var service = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, service);
        var project = (await service.TryCreateAsync("Partial observed run", Path.GetTempPath())).Project!;
        var workstream = project.Workstreams[0];
        var task = new RelayTask
        {
            WorkstreamId = workstream.Id,
            Prompt = "# CODEX_TASK\nPartial",
            Status = RelayTaskStatus.CodexRunning
        };
        state.BrowserBridge.Tasks.Add(task);
        workstream.CurrentTaskId = task.Id.ToString("D");
        workstream.CurrentState = WorkflowState.CodexRunning;

        var receipt = Receipt(project.Id, workstream.Id, task.Id, generation: 2);
        var result = CodexRunResult.Failure(
            receipt,
            CodexRunCompletionMode.Timeout,
            "codex_run_timeout",
            "timed out",
            [Output(0, "partial output")],
            isPartial: true,
            completedAtUtc: DateTimeOffset.UtcNow);

        var recorded = await bridge.RecordObservedCodexRunFailureAsync(receipt, result);

        Assert.IsFalse(recorded.Success);
        Assert.IsNotNull(recorded.Value);
        Assert.AreEqual(WorkflowState.NeedsAttention, workstream.CurrentState);
        Assert.AreEqual(RelayTaskStatus.Error, task.Status);
        Assert.AreEqual(1, task.CodexRunOutputCount);
        Assert.IsNotNull(task.CodexPartialResultPayload);
    }

    [TestMethod]
    public async Task TimeoutWithNoOutputsCannotTransitionToReadyForChatGpt()
    {
        var state = new ApplicationState();
        var service = new ProjectService(state, new MemoryStateStore(), new WorkflowStateMachine());
        var bridge = new BrowserBridgeService(state, service);
        var project = (await service.TryCreateAsync("Empty timeout", Path.GetTempPath())).Project!;
        var workstream = project.Workstreams[0];
        var task = new RelayTask
        {
            WorkstreamId = workstream.Id,
            Prompt = "# CODEX_TASK\nEmpty timeout",
            Status = RelayTaskStatus.CodexRunning
        };
        state.BrowserBridge.Tasks.Add(task);
        workstream.CurrentTaskId = task.Id.ToString("D");
        workstream.CurrentState = WorkflowState.CodexRunning;

        var receipt = Receipt(project.Id, workstream.Id, task.Id, generation: 4);
        var result = CodexRunResult.Failure(
            receipt,
            CodexRunCompletionMode.Timeout,
            "codex_run_timeout",
            "timed out");

        var recorded = await bridge.RecordObservedCodexRunFailureAsync(receipt, result);

        Assert.IsFalse(recorded.Success);
        Assert.AreEqual(WorkflowState.Error, workstream.CurrentState);
        Assert.AreEqual(RelayTaskStatus.Error, task.Status);
        Assert.IsNull(task.ResultPayload);
        Assert.IsNull(task.CodexPartialResultPayload);
    }

    [TestMethod]
    public async Task RunMetadataRoundTripsWhileResultBodyRemainsPayloadBacked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BlueRelayRunMetadataTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var state = new ApplicationState();
            var store = new JsonStateStore(Path.Combine(directory, "state.json"));
            var service = new ProjectService(state, store, new WorkflowStateMachine());
            var project = (await service.TryCreateAsync("Metadata round trip", directory)).Project!;
            var workstream = project.Workstreams[0];
            var task = new RelayTask
            {
                WorkstreamId = workstream.Id,
                Result = "result body",
                ResultPayload = new RelayPayload
                {
                    Path = "result.md",
                    Length = 11,
                    Sha256 = "hash"
                },
                CodexRunId = Guid.NewGuid(),
                CodexRunOutputCount = 2,
                CodexRunCompletedAt = DateTimeOffset.UtcNow,
                CodexRunCompletionMode = nameof(CodexRunCompletionMode.NativeRunControl),
                CodexRunCaptureMethodSummary = "NativeCopy=2",
                CodexPartialResultPayload = new RelayPayload { Path = "result.partial.md" }
            };
            state.BrowserBridge.Tasks.Add(task);
            workstream.CurrentTaskId = task.Id.ToString("D");
            await service.TrySaveAsync();

            var loaded = await store.LoadAsync();
            var loadedTask = loaded.State.BrowserBridge.Tasks.Single();
            Assert.IsNull(loadedTask.Result);
            Assert.AreEqual(task.CodexRunId, loadedTask.CodexRunId);
            Assert.AreEqual(2, loadedTask.CodexRunOutputCount);
            Assert.AreEqual(task.CodexRunCompletionMode, loadedTask.CodexRunCompletionMode);
            Assert.AreEqual(task.CodexRunCaptureMethodSummary, loadedTask.CodexRunCaptureMethodSummary);
            Assert.AreEqual("result.partial.md", loadedTask.CodexPartialResultPayload!.Path);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static CodexRunReceipt Receipt(Guid projectId, Guid workstreamId, Guid taskId, long generation) =>
        new(
            Guid.NewGuid(),
            projectId,
            workstreamId,
            taskId,
            generation,
            new IntPtr(123),
            42,
            DateTimeOffset.UtcNow,
            CodexComposerInjectionMode.ClipboardInlineVerified,
            new CodexRunBaseline(true, 0, []));

    private static CodexRunOutput Output(int sequence, string text) =>
        new(
            sequence,
            $"fingerprint-{sequence}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            CodexRunOutputKind.AssistantText,
            text,
            CodexRunCaptureMethod.UiaPlainText);

    private sealed class MemoryStateStore : IStateStore
    {
        public string FilePath => "memory://observed-run-tests";

        public Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StateLoadResult(new ApplicationState(), null));

        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
