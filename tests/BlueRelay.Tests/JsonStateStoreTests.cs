using BlueRelay.Models;
using BlueRelay.Persistence;

namespace BlueRelay.Tests;

[TestClass]
public sealed class JsonStateStoreTests
{
    private string _testDirectory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "BlueRelayTests", Guid.NewGuid().ToString("N"));
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
    public async Task SaveAndLoadRoundTripsProjectsIndependently()
    {
        var path = Path.Combine(_testDirectory, "state.json");
        var store = new JsonStateStore(path);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var state = new ApplicationState
        {
            Projects =
            [
                new Project
                {
                    Id = firstId,
                    Name = "First",
                    LocalPath = "C:\\First",
                    Workstreams = [new Workstream { Id = Guid.NewGuid(), ProjectId = firstId, Name = "First line", CurrentState = WorkflowState.ReadyForChatGPT }]
                },
                new Project
                {
                    Id = secondId,
                    Name = "Second",
                    LocalPath = "C:\\Second",
                    Workstreams = [new Workstream { Id = Guid.NewGuid(), ProjectId = secondId, Name = "Second line", CurrentState = WorkflowState.CodexRunning }]
                }
            ]
        };

        await store.SaveAsync(state);
        var result = await store.LoadAsync();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(2, result.State.Projects.Count);
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, result.State.Projects.Single(project => project.Id == firstId).Workstreams[0].CurrentState);
        Assert.AreEqual(WorkflowState.CodexRunning, result.State.Projects.Single(project => project.Id == secondId).Workstreams[0].CurrentState);
    }

    [TestMethod]
    public async Task OlderStateFilesKeepProjectsAndUseNewWindowDefaults()
    {
        var path = Path.Combine(_testDirectory, "state.json");
        Directory.CreateDirectory(_testDirectory);
        var projectId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            path,
            $$"""{"SchemaVersion":1,"Projects":[{"Id":"{{projectId}}","Name":"Legacy","LocalPath":"C:\\Legacy","CurrentState":"ReadyForCodex","ChatGPTTab":"tab-1","CodexSessionId":"session-1","CurrentTaskId":"task-1"}],"SelectedProjectId":"{{projectId}}","IsAlwaysOnTop":false}""");

        var result = await new JsonStateStore(path).LoadAsync();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(1, result.State.Projects.Count);
        Assert.AreEqual(projectId, result.State.Projects[0].Id);
        Assert.IsTrue(result.WasMigrated);
        Assert.AreEqual(3, result.State.SchemaVersion);
        Assert.AreEqual(1, result.State.Projects[0].Workstreams.Count);
        Assert.AreEqual(WorkflowState.ReadyForCodex, result.State.Projects[0].Workstreams[0].CurrentState);
        Assert.AreEqual(projectId, result.State.Projects[0].Workstreams[0].ProjectId);
        Assert.AreEqual("tab-1", result.State.Projects[0].Workstreams[0].ChatGPTTabId);
        Assert.AreEqual("session-1", result.State.Projects[0].Workstreams[0].CodexSessionId);
        Assert.AreEqual("task-1", result.State.Projects[0].Workstreams[0].CurrentTaskId);
        Assert.AreEqual(projectId, result.State.SelectedProjectId);
        Assert.IsNull(result.State.WindowLeft);
        Assert.IsNull(result.State.WindowTop);
        Assert.IsNull(result.State.WindowWidth);
        Assert.IsNull(result.State.WindowHeight);
        Assert.IsFalse(result.State.IsWindowCollapsed);
        Assert.IsFalse(result.State.IsAlwaysOnTop);

        await new JsonStateStore(path).SaveAsync(result.State);
        var migratedReload = await new JsonStateStore(path).LoadAsync();
        Assert.IsFalse(migratedReload.WasMigrated);
        Assert.AreEqual(3, migratedReload.State.SchemaVersion);
        Assert.AreEqual(WorkflowState.ReadyForCodex, migratedReload.State.Projects[0].Workstreams[0].CurrentState);
    }

    [TestMethod]
    public async Task WindowSettingsRoundTripWithState()
    {
        var path = Path.Combine(_testDirectory, "state.json");
        var state = new ApplicationState
        {
            WindowLeft = -1280.5,
            WindowTop = 48.25,
            WindowWidth = 612.5,
            WindowHeight = 488.75,
            IsWindowCollapsed = true
        };

        var store = new JsonStateStore(path);
        await store.SaveAsync(state);
        var result = await store.LoadAsync();

        Assert.AreEqual(-1280.5, result.State.WindowLeft);
        Assert.AreEqual(48.25, result.State.WindowTop);
        Assert.AreEqual(612.5, result.State.WindowWidth);
        Assert.AreEqual(488.75, result.State.WindowHeight);
        Assert.IsTrue(result.State.IsWindowCollapsed);
    }

    [TestMethod]
    public async Task CorruptJsonStartsFreshAndCreatesBackup()
    {
        var path = Path.Combine(_testDirectory, "state.json");
        Directory.CreateDirectory(_testDirectory);
        await File.WriteAllTextAsync(path, "{ not valid json");
        var store = new JsonStateStore(path);

        var result = await store.LoadAsync();

        Assert.IsNotNull(result.Warning);
        Assert.AreEqual(0, result.State.Projects.Count);
        Assert.IsTrue(Directory.GetFiles(_testDirectory, "state.json.corrupt-*.bak").Length == 1);
    }
}
