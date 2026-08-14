using BlueRelay.Models;
using BlueRelay.Services;

namespace BlueRelay.Tests;

[TestClass]
public sealed class WorkflowStateMachineTests
{
    private readonly WorkflowStateMachine _machine = new();

    [TestMethod]
    public void NormalWorkflowAllowsTheExpectedHandoffSequence()
    {
        Assert.IsTrue(_machine.CanTransition(WorkflowState.Idle, WorkflowState.ReadyForCodex));
        Assert.IsTrue(_machine.CanTransition(WorkflowState.ReadyForCodex, WorkflowState.CodexRunning));
        Assert.IsTrue(_machine.CanTransition(WorkflowState.CodexRunning, WorkflowState.ReadyForChatGPT));
        Assert.IsTrue(_machine.CanTransition(WorkflowState.ReadyForChatGPT, WorkflowState.ChatGPTReviewing));
        Assert.IsTrue(_machine.CanTransition(WorkflowState.ChatGPTReviewing, WorkflowState.Completed));
    }

    [TestMethod]
    public void NormalWorkflowRejectsSkippingCodex()
    {
        Assert.IsFalse(_machine.CanTransition(WorkflowState.Idle, WorkflowState.Completed));
        Assert.IsFalse(_machine.CanTransition(WorkflowState.CodexRunning, WorkflowState.Completed));
    }

    [TestMethod]
    public void ManualOverrideCanSetAnyStateForMvpTesting()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "Test", LocalPath = "C:\\" };

        Assert.IsTrue(_machine.TryTransition(project, WorkflowState.Completed, manualOverride: true, out var error));
        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual(WorkflowState.Completed, project.CurrentState);
    }
}
