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
    }

    [TestMethod]
    public void UnsupportedCultureFallsBackToEnglish()
    {
        var text = LocalizationService.ForCulture(CultureInfo.GetCultureInfo("fr-FR"));

        Assert.AreEqual("Waiting for a new task", text.GetStateLabel(WorkflowState.Idle));
        Assert.AreEqual("Project settings", text.Settings);
    }
}
