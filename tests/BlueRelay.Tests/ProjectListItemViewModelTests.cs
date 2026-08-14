using BlueRelay.Localization;
using BlueRelay.Models;
using BlueRelay.Presentation.ViewModels;

namespace BlueRelay.Tests;

[TestClass]
public sealed class ProjectListItemViewModelTests
{
    [TestMethod]
    public void DebugStateRequestsImmediateStatePersistenceAndRefreshDoesNotReapplyIt()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Test project",
            LocalPath = Environment.CurrentDirectory
        };
        var workstream = new Workstream
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "Test workstream",
            CurrentState = WorkflowState.Idle
        };
        project.Workstreams.Add(workstream);
        var item = new ProjectListItemViewModel(project, workstream, LocalizationService.ForCulture(new System.Globalization.CultureInfo("zh-CN")));
        var requestedStates = new List<WorkflowState>();
        item.StateChangeRequested += (_, state) => requestedStates.Add(state);

        item.DebugState = WorkflowState.CodexRunning;
        item.Refresh();

        CollectionAssert.AreEqual(new[] { WorkflowState.CodexRunning }, requestedStates);
        Assert.AreEqual(WorkflowState.Idle, item.DebugState);
    }
}
