using System.Globalization;
using BlueRelay.Models;

namespace BlueRelay.Localization;

public sealed class UiTextSet
{
    public required string ProductName { get; init; }
    public required string ProductSubtitle { get; init; }
    public required string AlwaysOnTopEnabled { get; init; }
    public required string AlwaysOnTopDisabled { get; init; }
    public required string Collapse { get; init; }
    public required string Expand { get; init; }
    public required string HideToTray { get; init; }
    public required string Settings { get; init; }
    public required string Workflow { get; init; }
    public required string WorkflowSubtitle { get; init; }
    public required string NextAction { get; init; }
    public required string Pending { get; init; }
    public required string CurrentTask { get; init; }
    public required string CurrentTaskNone { get; init; }
    public required string Projects { get; init; }
    public required string ProjectManagement { get; init; }
    public required string ProjectManagementSubtitle { get; init; }
    public required string AddProject { get; init; }
    public required string NewProject { get; init; }
    public required string EmptyTitle { get; init; }
    public required string EmptyDescription { get; init; }
    public required string NoProjects { get; init; }
    public required string ProjectDetails { get; init; }
    public required string Name { get; init; }
    public required string LocalPath { get; init; }
    public required string Browse { get; init; }
    public required string Edit { get; init; }
    public required string Delete { get; init; }
    public required string Cancel { get; init; }
    public required string SaveProject { get; init; }
    public required string Close { get; init; }
    public required string ApplyState { get; init; }
    public required string DeleteProjectTitle { get; init; }
    public required string DeleteProjectMessageFormat { get; init; }
    public required string TrayShow { get; init; }
    public required string TrayExit { get; init; }
    public required string ProjectCreated { get; init; }
    public required string ProjectUpdated { get; init; }
    public required string ProjectDeleted { get; init; }
    public required string ChangesDiscarded { get; init; }
    public required string WorkflowUpdated { get; init; }
    public required string CreateProjectHint { get; init; }
    public required string EditProjectHint { get; init; }

    public required IReadOnlyDictionary<WorkflowState, string> StateLabels { get; init; }
    public required IReadOnlyDictionary<WorkflowState, string> StateGuidance { get; init; }

    public string GetStateLabel(WorkflowState state) =>
        StateLabels.TryGetValue(state, out var value) ? value : StateLabels[WorkflowState.Error];

    public string GetStateGuidance(WorkflowState state) =>
        StateGuidance.TryGetValue(state, out var value) ? value : StateGuidance[WorkflowState.Error];
}

public static class LocalizationService
{
    private static readonly UiTextSet English = CreateEnglish();
    private static readonly UiTextSet Chinese = CreateChinese();

    public static UiTextSet Current => ForCulture(CultureInfo.CurrentUICulture);

    public static UiTextSet ForCulture(CultureInfo culture)
    {
        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    private static UiTextSet CreateEnglish() => new()
    {
        ProductName = "BlueRelay",
        ProductSubtitle = "ChatGPT ↔ Codex workflow",
        AlwaysOnTopEnabled = "Always on top: on",
        AlwaysOnTopDisabled = "Always on top: off",
        Collapse = "Collapse",
        Expand = "Expand",
        HideToTray = "Hide to tray",
        Settings = "Project settings",
        Workflow = "Workflow",
        WorkflowSubtitle = "See what is happening and who acts next.",
        NextAction = "Next action",
        Pending = "Pending",
        CurrentTask = "Current task",
        CurrentTaskNone = "No task assigned",
        Projects = "Workstreams",
        ProjectManagement = "Project management",
        ProjectManagementSubtitle = "Add or edit project records. Local files are never changed.",
        AddProject = "Add project",
        NewProject = "New project",
        EmptyTitle = "No workflow yet",
        EmptyDescription = "Add a project to keep its handoff state visible here.",
        NoProjects = "No projects yet.",
        ProjectDetails = "Project details",
        Name = "Name",
        LocalPath = "Local path",
        Browse = "Browse",
        Edit = "Edit",
        Delete = "Delete",
        Cancel = "Cancel",
        SaveProject = "Save project",
        Close = "Close",
        ApplyState = "Apply state",
        DeleteProjectTitle = "Delete project record?",
        DeleteProjectMessageFormat = "Remove '{0}' from BlueRelay?\n\nThe local directory will not be deleted.",
        TrayShow = "Show BlueRelay",
        TrayExit = "Exit",
        ProjectCreated = "Project created.",
        ProjectUpdated = "Project updated.",
        ProjectDeleted = "Project record deleted. No local files were changed.",
        ChangesDiscarded = "Changes discarded.",
        WorkflowUpdated = "Workflow state updated manually.",
        CreateProjectHint = "Create a project record. BlueRelay will not change files in the selected directory.",
        EditProjectHint = "Edit the project record, then save your changes.",
        StateLabels = new Dictionary<WorkflowState, string>
        {
            [WorkflowState.Idle] = "Waiting for a new task",
            [WorkflowState.ReadyForCodex] = "Next: send to Codex",
            [WorkflowState.CodexRunning] = "Codex is running",
            [WorkflowState.ReadyForChatGPT] = "Next: send to ChatGPT",
            [WorkflowState.ChatGPTReviewing] = "Waiting for ChatGPT review",
            [WorkflowState.Completed] = "Round completed",
            [WorkflowState.Error] = "Needs attention"
        },
        StateGuidance = new Dictionary<WorkflowState, string>
        {
            [WorkflowState.Idle] = "Start a new task when ready.",
            [WorkflowState.ReadyForCodex] = "The task is ready for Codex.",
            [WorkflowState.CodexRunning] = "No action is needed right now.",
            [WorkflowState.ReadyForChatGPT] = "Hand the Codex result back for review.",
            [WorkflowState.ChatGPTReviewing] = "Wait for the next review decision.",
            [WorkflowState.Completed] = "This task round is closed.",
            [WorkflowState.Error] = "Resolve the issue before continuing."
        }
    };

    private static UiTextSet CreateChinese() => new()
    {
        ProductName = "BlueRelay",
        ProductSubtitle = "ChatGPT ↔ Codex 工作流",
        AlwaysOnTopEnabled = "置顶：开",
        AlwaysOnTopDisabled = "置顶：关",
        Collapse = "折叠",
        Expand = "展开",
        HideToTray = "隐藏到托盘",
        Settings = "项目设置",
        Workflow = "工作流",
        WorkflowSubtitle = "查看当前进度和下一位执行者。",
        NextAction = "下一步",
        Pending = "待处理",
        CurrentTask = "当前任务",
        CurrentTaskNone = "未分配任务",
        Projects = "工作流",
        ProjectManagement = "项目管理",
        ProjectManagementSubtitle = "添加或编辑项目记录，不会修改本地文件。",
        AddProject = "添加项目",
        NewProject = "新建项目",
        EmptyTitle = "还没有工作流",
        EmptyDescription = "添加项目后，就能在这里看到它的交接状态。",
        NoProjects = "还没有项目。",
        ProjectDetails = "项目详情",
        Name = "名称",
        LocalPath = "本地路径",
        Browse = "浏览",
        Edit = "编辑",
        Delete = "删除",
        Cancel = "取消",
        SaveProject = "保存项目",
        Close = "关闭",
        ApplyState = "应用状态",
        DeleteProjectTitle = "删除项目记录？",
        DeleteProjectMessageFormat = "要从 BlueRelay 删除“{0}”吗？\n\n不会删除本地目录。",
        TrayShow = "显示 BlueRelay",
        TrayExit = "退出",
        ProjectCreated = "项目已创建。",
        ProjectUpdated = "项目已更新。",
        ProjectDeleted = "项目记录已删除，没有修改本地文件。",
        ChangesDiscarded = "已放弃修改。",
        WorkflowUpdated = "工作流状态已手动更新。",
        CreateProjectHint = "创建项目记录。BlueRelay 不会修改所选目录中的文件。",
        EditProjectHint = "编辑项目记录，然后保存修改。",
        StateLabels = new Dictionary<WorkflowState, string>
        {
            [WorkflowState.Idle] = "等待新任务",
            [WorkflowState.ReadyForCodex] = "下一步：发送给 Codex",
            [WorkflowState.CodexRunning] = "Codex 运行中",
            [WorkflowState.ReadyForChatGPT] = "下一步：发送给 ChatGPT",
            [WorkflowState.ChatGPTReviewing] = "等待 ChatGPT 审查",
            [WorkflowState.Completed] = "本轮已完成",
            [WorkflowState.Error] = "需要处理"
        },
        StateGuidance = new Dictionary<WorkflowState, string>
        {
            [WorkflowState.Idle] = "准备好后开始新任务。",
            [WorkflowState.ReadyForCodex] = "任务已准备好发送给 Codex。",
            [WorkflowState.CodexRunning] = "当前不需要操作。",
            [WorkflowState.ReadyForChatGPT] = "把 Codex 的结果交回审查。",
            [WorkflowState.ChatGPTReviewing] = "等待下一步审查决定。",
            [WorkflowState.Completed] = "本轮任务已结束。",
            [WorkflowState.Error] = "处理问题后再继续。"
        }
    };
}
