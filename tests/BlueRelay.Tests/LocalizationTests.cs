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
        Assert.AreEqual("项目设置", text.Settings);
        Assert.AreEqual("默认工作流", text.DefaultWorkstream);
        Assert.AreEqual("刷新 Git 信息", text.RefreshGit);
    }

    [TestMethod]
    public void UnsupportedCultureFallsBackToEnglish()
    {
        var text = LocalizationService.ForCulture(CultureInfo.GetCultureInfo("fr-FR"));

        Assert.AreEqual("Waiting for a new task", text.GetStateLabel(WorkflowState.Idle));
        Assert.AreEqual("Project settings", text.Settings);
        Assert.AreEqual("Default", text.DefaultWorkstream);
    }
}
