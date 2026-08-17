using System.Globalization;
using BlueRelay.Localization;
using BlueRelay.Models;

namespace BlueRelay.Tests;

[TestClass]
public sealed class LocalizationTests
{
    [TestMethod]
    public void ChineseCultureUsesChineseWorkflowCopy()
    {
        var text = LocalizationService.ForCulture(CultureInfo.GetCultureInfo("zh-CN"));

        Assert.AreEqual("等待新任务", text.GetStateLabel(WorkflowState.Idle));
        Assert.AreEqual("项目管理", text.Settings);
        Assert.AreEqual("默认工作流", text.DefaultWorkstream);
        Assert.AreEqual("刷新 Git 信息", text.RefreshGit);
        Assert.AreEqual("个工作流", text.WorkstreamCountLabel);
        Assert.AreEqual("暂无会话绑定", text.NoSession);
        Assert.AreEqual("结束本轮", text.CompleteCurrentRound);
        Assert.AreEqual("清空当前任务", text.ClearCurrentTask);
        StringAssert.Contains(text.ClearCurrentTaskMessage, "Prompt");
    }

    [TestMethod]
    public void UnsupportedCultureFallsBackToEnglish()
    {
        var text = LocalizationService.ForCulture(CultureInfo.GetCultureInfo("fr-FR"));

        Assert.AreEqual("Waiting for a new task", text.GetStateLabel(WorkflowState.Idle));
        Assert.AreEqual("Project management", text.Settings);
        Assert.AreEqual("Default", text.DefaultWorkstream);
        Assert.AreEqual("workstreams", text.WorkstreamCountLabel);
        Assert.AreEqual("No session linked", text.NoSession);
        Assert.AreEqual("Complete current round", text.CompleteCurrentRound);
        Assert.AreEqual("Clear current task", text.ClearCurrentTask);
        StringAssert.Contains(text.ClearCurrentTaskMessage, "Prompt");
    }
}
