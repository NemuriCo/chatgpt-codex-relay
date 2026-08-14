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
                new Project { Id = firstId, Name = "First", LocalPath = "C:\\First", CurrentState = WorkflowState.ReadyForChatGPT },
                new Project { Id = secondId, Name = "Second", LocalPath = "C:\\Second", CurrentState = WorkflowState.CodexRunning }
            ]
        };

        await store.SaveAsync(state);
        var result = await store.LoadAsync();

        Assert.IsNull(result.Warning);
        Assert.AreEqual(2, result.State.Projects.Count);
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, result.State.Projects.Single(project => project.Id == firstId).CurrentState);
        Assert.AreEqual(WorkflowState.CodexRunning, result.State.Projects.Single(project => project.Id == secondId).CurrentState);
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
