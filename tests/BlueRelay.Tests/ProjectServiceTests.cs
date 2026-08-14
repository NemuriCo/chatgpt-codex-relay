using BlueRelay.Models;
using BlueRelay.Persistence;
using BlueRelay.Services;

namespace BlueRelay.Tests;

[TestClass]
public sealed class ProjectServiceTests
{
    private string _testDirectory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "BlueRelayTests", Guid.NewGuid().ToString("N"));
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
    public async Task ProjectsKeepIndependentStatesAcrossReloadAndDeleteDoesNotTouchDirectory()
    {
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "First")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Second")).FullName;
        var statePath = Path.Combine(_testDirectory, "state.json");
        var state = new ApplicationState();
        var store = new JsonStateStore(statePath);
        var service = new ProjectService(state, store, new WorkflowStateMachine());

        var firstResult = await service.TryCreateAsync("First", firstDirectory);
        var secondResult = await service.TryCreateAsync("Second", secondDirectory);
        Assert.IsTrue(firstResult.Success, firstResult.Error);
        Assert.IsTrue(secondResult.Success, secondResult.Error);
        Assert.IsNotNull(firstResult.Project);
        Assert.IsNotNull(secondResult.Project);
        var first = firstResult.Project!;
        var second = secondResult.Project!;
        var firstStateResult = await service.TryChangeStateAsync(first.Id, WorkflowState.ReadyForChatGPT, manualOverride: true);
        var secondStateResult = await service.TryChangeStateAsync(second.Id, WorkflowState.CodexRunning, manualOverride: true);
        Assert.IsTrue(firstStateResult.Success, firstStateResult.Error);
        Assert.IsTrue(secondStateResult.Success, secondStateResult.Error);

        var reloaded = await store.LoadAsync();
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, reloaded.State.Projects.Single(project => project.Id == first.Id).CurrentState);
        Assert.AreEqual(WorkflowState.CodexRunning, reloaded.State.Projects.Single(project => project.Id == second.Id).CurrentState);

        var markerPath = Path.Combine(firstDirectory, "user-file.txt");
        await File.WriteAllTextAsync(markerPath, "must remain");
        var deleteResult = await service.TryDeleteAsync(first.Id);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        Assert.IsTrue(Directory.Exists(firstDirectory));
        Assert.IsTrue(File.Exists(markerPath));
        Assert.IsNotNull(service.Find(second.Id));
    }

    [TestMethod]
    public async Task InvalidProjectRecordsAreRejected()
    {
        var state = new ApplicationState();
        var store = new JsonStateStore(Path.Combine(_testDirectory, "state.json"));
        var service = new ProjectService(state, store, new WorkflowStateMachine());

        var emptyNameResult = await service.TryCreateAsync("", _testDirectory);
        StringAssert.Contains(emptyNameResult.Error, "name");
        Assert.IsFalse(emptyNameResult.Success);
        var missingPathResult = await service.TryCreateAsync("Missing", Path.Combine(_testDirectory, "missing"));
        StringAssert.Contains(missingPathResult.Error, "existing directory");
        Assert.IsFalse(missingPathResult.Success);
    }

    [TestMethod]
    public async Task FirstLaunchLoadsEmptyStateAndPersistsItWithoutBlocking()
    {
        var statePath = Path.Combine(_testDirectory, "state.json");
        var store = new JsonStateStore(statePath);
        var stateLoadResult = await store.LoadAsync();
        var service = new ProjectService(stateLoadResult.State, store, new WorkflowStateMachine());

        Assert.IsFalse(File.Exists(statePath));
        Assert.AreEqual(0, stateLoadResult.State.Projects.Count);

        var saveResult = await service.TrySaveAsync();

        Assert.IsTrue(saveResult.Success, saveResult.Error);
        Assert.IsTrue(File.Exists(statePath));
        var reloaded = await store.LoadAsync();
        Assert.AreEqual(0, reloaded.State.Projects.Count);
    }
}
