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
    public async Task NewProjectsGetDefaultWorkstreamsAndKeepStatesIndependent()
    {
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "First")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Second")).FullName;
        var store = new JsonStateStore(Path.Combine(_testDirectory, "state.json"));
        var state = new ApplicationState();
        var service = new ProjectService(state, store, new WorkflowStateMachine());

        var firstResult = await service.TryCreateAsync("First", firstDirectory);
        var secondResult = await service.TryCreateAsync("Second", secondDirectory);
        Assert.IsTrue(firstResult.Success, firstResult.Error);
        Assert.IsTrue(secondResult.Success, secondResult.Error);
        var first = firstResult.Project!;
        var second = secondResult.Project!;
        Assert.AreEqual(1, first.Workstreams.Count);
        Assert.AreEqual(Workstream.DefaultName, first.Workstreams[0].Name);

        var firstStateResult = await service.TryChangeStateAsync(first.Id, first.Workstreams[0].Id, WorkflowState.ReadyForChatGPT, manualOverride: true);
        var secondStateResult = await service.TryChangeStateAsync(second.Id, second.Workstreams[0].Id, WorkflowState.CodexRunning, manualOverride: true);
        Assert.IsTrue(firstStateResult.Success, firstStateResult.Error);
        Assert.IsTrue(secondStateResult.Success, secondStateResult.Error);

        var reloaded = await store.LoadAsync();
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, reloaded.State.Projects.Single(project => project.Id == first.Id).Workstreams[0].CurrentState);
        Assert.AreEqual(WorkflowState.CodexRunning, reloaded.State.Projects.Single(project => project.Id == second.Id).Workstreams[0].CurrentState);

        var markerPath = Path.Combine(firstDirectory, "user-file.txt");
        await File.WriteAllTextAsync(markerPath, "must remain");
        var deleteResult = await service.TryDeleteAsync(first.Id);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);
        Assert.IsTrue(Directory.Exists(firstDirectory));
        Assert.IsTrue(File.Exists(markerPath));
        Assert.IsNotNull(service.Find(second.Id));
    }

    [TestMethod]
    public async Task MultipleWorkstreamsHaveIndependentStatesAndCannotRemoveTheLastOne()
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Project")).FullName;
        var service = new ProjectService(
            new ApplicationState(),
            new JsonStateStore(Path.Combine(_testDirectory, "state.json")),
            new WorkflowStateMachine());
        var project = (await service.TryCreateAsync("Project", projectDirectory)).Project!;
        var defaultWorkstream = project.Workstreams[0];

        var secondResult = await service.TryCreateWorkstreamAsync(project.Id, "Notifications");
        Assert.IsTrue(secondResult.Success, secondResult.Error);
        var second = secondResult.Workstream!;
        Assert.IsTrue((await service.TryChangeStateAsync(project.Id, defaultWorkstream.Id, WorkflowState.ReadyForChatGPT, true)).Success);
        Assert.IsTrue((await service.TryChangeStateAsync(project.Id, second.Id, WorkflowState.CodexRunning, true)).Success);

        Assert.AreEqual(WorkflowState.ReadyForChatGPT, defaultWorkstream.CurrentState);
        Assert.AreEqual(WorkflowState.CodexRunning, second.CurrentState);

        var renameResult = await service.TryRenameWorkstreamAsync(project.Id, second.Id, "Notifications v2");
        Assert.IsTrue(renameResult.Success, renameResult.Error);
        Assert.AreEqual("Notifications v2", second.Name);

        var deleteResult = await service.TryDeleteWorkstreamAsync(project.Id, second.Id);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);
        Assert.AreEqual(1, project.Workstreams.Count);
        var lastDeleteResult = await service.TryDeleteWorkstreamAsync(project.Id, defaultWorkstream.Id);
        Assert.IsFalse(lastDeleteResult.Success);
    }

    [TestMethod]
    public async Task InvalidProjectAndWorkstreamRecordsAreRejected()
    {
        var service = new ProjectService(
            new ApplicationState(),
            new JsonStateStore(Path.Combine(_testDirectory, "state.json")),
            new WorkflowStateMachine());

        var emptyNameResult = await service.TryCreateAsync("", _testDirectory);
        StringAssert.Contains(emptyNameResult.Error, "name");
        Assert.IsFalse(emptyNameResult.Success);
        var missingPathResult = await service.TryCreateAsync("Missing", Path.Combine(_testDirectory, "missing"));
        StringAssert.Contains(missingPathResult.Error, "existing directory");
        Assert.IsFalse(missingPathResult.Success);

        var projectDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Valid")).FullName;
        var project = (await service.TryCreateAsync("Valid", projectDirectory)).Project!;
        var duplicateResult = await service.TryCreateWorkstreamAsync(project.Id, Workstream.DefaultName);
        Assert.IsFalse(duplicateResult.Success);
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
