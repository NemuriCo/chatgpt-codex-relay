using System.Net;
using System.Net.Http.Json;
using BlueRelay.Models;
using BlueRelay.Persistence;
using BlueRelay.Services;
using BlueRelay.Services.Bridges;

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
            "installation-a", "tab-a", "# CODEX_TASK\nImplement First", "https://chatgpt.com/c/first", "first", "First tab"));
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
        Assert.IsTrue((await bridge.AcknowledgeHandoffAsync(handoff.Value.CommandId, true)).Success);
        Assert.AreEqual(WorkflowState.ChatGPTReviewing, first.Workstreams[0].CurrentState);
        Assert.AreEqual(WorkflowState.Idle, second.Workstreams[0].CurrentState);
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
}
