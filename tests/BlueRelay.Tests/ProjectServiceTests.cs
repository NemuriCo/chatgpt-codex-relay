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

        Assert.IsTrue(service.TryCreate("First", firstDirectory, out var first, out var firstError), firstError);
        Assert.IsTrue(service.TryCreate("Second", secondDirectory, out var second, out var secondError), secondError);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsTrue(service.TryChangeState(first!.Id, WorkflowState.ReadyForChatGPT, manualOverride: true, out var firstStateError), firstStateError);
        Assert.IsTrue(service.TryChangeState(second!.Id, WorkflowState.CodexRunning, manualOverride: true, out var secondStateError), secondStateError);

        var reloaded = await store.LoadAsync();
        Assert.AreEqual(WorkflowState.ReadyForChatGPT, reloaded.State.Projects.Single(project => project.Id == first.Id).CurrentState);
        Assert.AreEqual(WorkflowState.CodexRunning, reloaded.State.Projects.Single(project => project.Id == second.Id).CurrentState);

        var markerPath = Path.Combine(firstDirectory, "user-file.txt");
        await File.WriteAllTextAsync(markerPath, "must remain");
        Assert.IsTrue(service.TryDelete(first.Id, out var deleteError), deleteError);

        Assert.IsTrue(Directory.Exists(firstDirectory));
        Assert.IsTrue(File.Exists(markerPath));
        Assert.IsNotNull(service.Find(second.Id));
    }

    [TestMethod]
    public void InvalidProjectRecordsAreRejected()
    {
        var state = new ApplicationState();
        var store = new JsonStateStore(Path.Combine(_testDirectory, "state.json"));
        var service = new ProjectService(state, store, new WorkflowStateMachine());

        Assert.IsFalse(service.TryCreate("", _testDirectory, out _, out var emptyNameError));
        StringAssert.Contains(emptyNameError, "name");
        Assert.IsFalse(service.TryCreate("Missing", Path.Combine(_testDirectory, "missing"), out _, out var missingPathError));
        StringAssert.Contains(missingPathError, "existing directory");
    }
}
