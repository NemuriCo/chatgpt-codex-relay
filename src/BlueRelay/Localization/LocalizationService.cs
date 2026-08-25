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
    public required string TaskPrompt { get; init; }
    public required string TaskResult { get; init; }
    public required string CurrentTaskNone { get; init; }
    public required string Projects { get; init; }
    public required string Workstream { get; init; }
    public required string DefaultWorkstream { get; init; }
    public required string WorkstreamManagement { get; init; }
    public required string WorkstreamCountLabel { get; init; }
    public required string NoSession { get; init; }
    public required string DebugState { get; init; }
    public required string NewWorkstream { get; init; }
    public required string RenameWorkstream { get; init; }
    public required string DeleteWorkstream { get; init; }
    public required string WorkstreamName { get; init; }
    public required string DeleteWorkstreamTitle { get; init; }
    public required string DeleteWorkstreamMessageFormat { get; init; }
    public required string LastWorkstreamRequired { get; init; }
    public required string WorkstreamCreated { get; init; }
    public required string WorkstreamUpdated { get; init; }
    public required string WorkstreamDeleted { get; init; }
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
    public required string Replace { get; init; }
    public required string SaveProject { get; init; }
    public required string SaveWorkstream { get; init; }
    public required string Close { get; init; }
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
    public required string ChooseProjectDirectory { get; init; }
    public required string GitDetected { get; init; }
    public required string GitNotDetected { get; init; }
    public required string GitUnavailable { get; init; }
    public required string GitRepositoryHint { get; init; }
    public required string GitNotRepositoryHint { get; init; }
    public required string GitUnavailableHint { get; init; }
    public required string RepositoryRoot { get; init; }
    public required string SelectedFolder { get; init; }
    public required string UseRepositoryRoot { get; init; }
    public required string RefreshGit { get; init; }
    public required string BrowserBridge { get; init; }
    public required string CodexAppServer { get; init; }
    public required string BrowserBridgeRunning { get; init; }
    public required string BrowserBridgeUnavailable { get; init; }
    public required string BridgeEndpoint { get; init; }
    public required string PairingCodeNotGenerated { get; init; }
    public required string GeneratePairingCode { get; init; }
    public required string ResetPairing { get; init; }
    public required string PairingInstructions { get; init; }
    public required string BrowserConnected { get; init; }
    public required string BrowserDisconnected { get; init; }
    public required string BrowserNotBound { get; init; }
    public required string ChatGPTBinding { get; init; }
    public required string ChatGPTNotBound { get; init; }
    public required string ChatGPTConversationChanged { get; init; }
    public required string CodexSession { get; init; }
    public required string CodexNotBound { get; init; }
    public required string CodexBound { get; init; }
    public required string CodexConflict { get; init; }
    public required string NewCodexSessionAndRetry { get; init; }
    public required string NewCodexSessionAndRetryTitle { get; init; }
    public required string NewCodexSessionAndRetryMessage { get; init; }
    public required string CodexSessionRetried { get; init; }
    public required string CopyCodexThreadId { get; init; }
    public required string CodexThreadIdCopied { get; init; }
    public required string SendToCodex { get; init; }
    public required string FillCodex { get; init; }
    public required string CodexComposerFilled { get; init; }
    public required string CodexComposerFilledClipboardRestoreFailed { get; init; }
    public required string CodexComposerFilledClipboardWarning { get; init; }
    public required string CodexComposerClipboardReferenceAccepted { get; init; }
    public required string CodexComposerClipboardReferenceAcceptedRestoreFailed { get; init; }
    public required string CodexComposerClipboardRestoreFailed { get; init; }
    public required string CodexComposerVerificationFailed { get; init; }
    public required string CodexComposerNotFound { get; init; }
    public required string CodexComposerClipboardUnsafe { get; init; }
    public required string CodexComposerClipboardWriteFailed { get; init; }
    public required string CodexComposerTaskEmpty { get; init; }
    public required string CodexComposerInjectionFailed { get; init; }
    public required string CodexComposerSearching { get; init; }
    public required string CodexComposerProbeTimeout { get; init; }
    public required string CodexComposerBusy { get; init; }
    public required string CodexComposerCancelled { get; init; }
    public required string CodexComposerForegroundFailed { get; init; }
    public required string CodexComposerExistingTextTitle { get; init; }
    public required string CodexComposerExistingTextMessage { get; init; }
    public required string CodexComposerContentUnknownTitle { get; init; }
    public required string CodexComposerContentUnknownMessage { get; init; }
    public required string FocusedComposerProbeTitle { get; init; }
    public required string FocusedComposerProbeInstructions { get; init; }
    public required string FocusedComposerProbeHotkey { get; init; }
    public required string FocusedComposerProbePrepare { get; init; }
    public required string FocusedComposerProbeRunning { get; init; }
    public required string FocusedComposerProbeCompleted { get; init; }
    public required string FocusedComposerProbeNotCodex { get; init; }
    public required string FocusedComposerProbeUnavailable { get; init; }
    public required string FocusedComposerProbeTimeout { get; init; }
    public required string FocusedComposerProbeBusy { get; init; }
    public required string FocusedComposerProbeCancelled { get; init; }
    public required string FocusedComposerProbeFailed { get; init; }
    public required string RetryCodex { get; init; }
    public required string CodexRunning { get; init; }
    public required string CodexCancel { get; init; }
    public required string CodexCancelled { get; init; }
    public required string CodexProgress { get; init; }
    public required string CodexApprovalTitle { get; init; }
    public required string CodexApprovalMessage { get; init; }
    public required string Allow { get; init; }
    public required string ResetCodexThread { get; init; }
    public required string ResetCodexThreadTitle { get; init; }
    public required string ResetCodexThreadMessage { get; init; }
    public required string CodexThreadReset { get; init; }
    public required string AddUserNote { get; init; }
    public required string ResultNote { get; init; }
    public required string SendToChatGPT { get; init; }
    public required string RetrySendToChatGPT { get; init; }
    public required string CompleteCurrentRound { get; init; }
    public required string ClearCurrentTask { get; init; }
    public required string ClearCurrentTaskTitle { get; init; }
    public required string ClearCurrentTaskMessage { get; init; }
    public required string CurrentRoundCompleted { get; init; }
    public required string CurrentTaskCleared { get; init; }
    public required string CodexSimulationStarted { get; init; }
    public required string HandoffQueued { get; init; }
    public required string HandoffFailed { get; init; }
    public required string HandoffFallback { get; init; }
    public required string HandoffDelivered { get; init; }
    public required string SimulatedResultSaved { get; init; }
    public required string Debug { get; init; }
    public required string ManualState { get; init; }
    public required string ApplyDebugState { get; init; }
    public required string SimulateCodexResult { get; init; }
    public required string SimulatedResultTitle { get; init; }
    public required string SimulatedResultHint { get; init; }
    public required string ResetPairingTitle { get; init; }
    public required string ResetPairingMessage { get; init; }
    public required string PairingReset { get; init; }
    public required string WorkstreamUnbound { get; init; }
    public required string UnbindWorkstream { get; init; }
    public required string PairingCodeLabel { get; init; }

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
        Settings = "Project management",
        Workflow = "Workflow",
        WorkflowSubtitle = "See what is happening and who acts next.",
        NextAction = "Next action",
        Pending = "Pending",
        CurrentTask = "Current task",
        TaskPrompt = "Task prompt",
        TaskResult = "Result",
        CurrentTaskNone = "No task assigned",
        Projects = "Workstreams",
        Workstream = "Workstream",
        DefaultWorkstream = "Default",
        WorkstreamManagement = "Workstreams",
        WorkstreamCountLabel = "workstreams",
        NoSession = "No session linked",
        DebugState = "Debug state",
        NewWorkstream = "New workstream",
        RenameWorkstream = "Rename",
        DeleteWorkstream = "Delete",
        WorkstreamName = "Workstream name",
        DeleteWorkstreamTitle = "Delete workstream?",
        DeleteWorkstreamMessageFormat = "Remove '{0}' from this project?",
        LastWorkstreamRequired = "At least one workstream must remain in a project.",
        WorkstreamCreated = "Workstream created.",
        WorkstreamUpdated = "Workstream updated.",
        WorkstreamDeleted = "Workstream deleted.",
        ProjectManagement = "Project management",
        ProjectManagementSubtitle = "Manage project records and workstreams. Local files are never changed.",
        AddProject = "Add project",
        NewProject = "New project",
        EmptyTitle = "No workflow yet",
        EmptyDescription = "Add a project and BlueRelay will show the ChatGPT ↔ Codex handoff state here.",
        NoProjects = "No projects yet.",
        ProjectDetails = "Project details",
        Name = "Name",
        LocalPath = "Local path",
        Browse = "Browse",
        Edit = "Edit",
        Delete = "Delete",
        Cancel = "Cancel",
        Replace = "Replace",
        SaveProject = "Save project",
        SaveWorkstream = "Save workstream",
        Close = "Close",
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
        ChooseProjectDirectory = "Choose the local project directory.",
        GitDetected = "Git repository detected",
        GitNotDetected = "Git repository not detected",
        GitUnavailable = "Git not detected",
        GitRepositoryHint = "The repository root and name were filled from Git.",
        GitNotRepositoryHint = "No Git repository was found. The selected folder will be used.",
        GitUnavailableHint = "Git is unavailable. The selected folder name will be used.",
        RepositoryRoot = "Repository root",
        SelectedFolder = "Selected folder",
        UseRepositoryRoot = "Use repository root",
        RefreshGit = "Refresh Git info",
        BrowserBridge = "Browser Bridge",
        CodexAppServer = "Codex App Server",
        BrowserBridgeRunning = "Browser Bridge running",
        BrowserBridgeUnavailable = "Browser Bridge unavailable",
        BridgeEndpoint = "Local endpoint",
        PairingCodeNotGenerated = "Generate a code",
        GeneratePairingCode = "Generate pairing code",
        ResetPairing = "Reset pairing",
        PairingInstructions = "In the extension popup, enter this one-time code. It expires in five minutes.",
        BrowserConnected = "ChatGPT • connected",
        BrowserDisconnected = "ChatGPT • disconnected",
        BrowserNotBound = "ChatGPT • not bound",
        ChatGPTBinding = "ChatGPT",
        ChatGPTNotBound = "Not bound",
        ChatGPTConversationChanged = "Conversation changed — rebind required",
        CodexSession = "Codex",
        CodexNotBound = "No Codex session",
        CodexBound = "Session linked",
        CodexConflict = "Session used by another process",
        NewCodexSessionAndRetry = "New Codex session and retry",
        NewCodexSessionAndRetryTitle = "Start a new Codex session and retry?",
        NewCodexSessionAndRetryMessage = "The current task and ChatGPT pairing will be kept. Only the Codex session will be replaced, then the task will be retried.",
        CodexSessionRetried = "A new Codex session was started and the task was retried.",
        CopyCodexThreadId = "Copy Codex thread ID",
        CodexThreadIdCopied = "Codex thread ID copied.",
        SendToCodex = "Confirm send",
        FillCodex = "Fill into Codex",
        CodexComposerFilled = "Filled into Codex. Review it and send manually.",
        CodexComposerFilledClipboardRestoreFailed = "Filled into Codex, but the original plain-text clipboard could not be restored.",
        CodexComposerFilledClipboardWarning = "Filled into Codex, but the original clipboard contents could not be fully preserved or restored.",
        CodexComposerClipboardReferenceAccepted = "The long text was accepted by Codex as a pasted-text reference. Review it and send manually.",
        CodexComposerClipboardReferenceAcceptedRestoreFailed = "The long text was accepted by Codex as a pasted-text reference, but the original plain-text clipboard could not be restored.",
        CodexComposerClipboardRestoreFailed = "Codex filling did not complete, and the original plain-text clipboard could not be restored.",
        CodexComposerVerificationFailed = "The Codex input box did not contain the complete task text. Try again.",
        CodexComposerNotFound = "No editable Codex input box was found.\nOpen a Codex session and try again.",
        CodexComposerClipboardUnsafe = "The clipboard could not be used safely for Codex input. Close other clipboard content and try again.",
        CodexComposerClipboardWriteFailed = "BlueRelay could not verify the task text in the clipboard. Try again.",
        CodexComposerTaskEmpty = "The current task is empty and could not be filled into Codex.",
        CodexComposerInjectionFailed = "BlueRelay could not fill the Codex input box. Try again.",
        CodexComposerSearching = "Looking for the Codex input box…",
        CodexComposerProbeTimeout = "Looking for the Codex input box timed out. Make sure a Codex session is open and try again.",
        CodexComposerBusy = "A Codex input operation is already in progress. Please wait and try again.",
        CodexComposerCancelled = "Filling the Codex input box was cancelled.",
        CodexComposerForegroundFailed = "Codex could not be activated as the foreground window. Bring Codex to the front and try again.",
        CodexComposerExistingTextTitle = "Replace existing Codex text?",
        CodexComposerExistingTextMessage = "The Codex composer already contains text. Replace it with the current task? The existing text will be overwritten.",
        CodexComposerContentUnknownTitle = "Confirm replacing Codex composer content",
        CodexComposerContentUnknownMessage = "BlueRelay could not safely determine whether the Codex composer is empty. Replace its content only if you are sure.",
        FocusedComposerProbeTitle = "Focused Composer UIA Probe",
        FocusedComposerProbeInstructions = "Click the Codex composer first, then press the global hotkey without clicking BlueRelay.",
        FocusedComposerProbeHotkey = "Hotkey: Ctrl + Alt + B",
        FocusedComposerProbePrepare = "Prepare composer probe",
        FocusedComposerProbeRunning = "Reading focused Codex UI Automation metadata…",
        FocusedComposerProbeCompleted = "Focused element belongs to Codex Desktop.",
        FocusedComposerProbeNotCodex = "The current focus is not in Codex Desktop.",
        FocusedComposerProbeUnavailable = "No focused UI Automation element was available.",
        FocusedComposerProbeTimeout = "Focused composer probe timed out. Try again with the Codex composer focused.",
        FocusedComposerProbeBusy = "A focused composer probe is already running.",
        FocusedComposerProbeCancelled = "Focused composer probe was cancelled.",
        FocusedComposerProbeFailed = "Focused composer probe failed. Try again.",
        RetryCodex = "Retry Codex task",
        CodexRunning = "Codex running",
        CodexCancel = "Cancel Codex task",
        CodexCancelled = "Codex task cancelled.",
        CodexProgress = "Progress",
        CodexApprovalTitle = "Codex approval request",
        CodexApprovalMessage = "Codex is requesting permission to continue. Review the request and choose whether to allow it.",
        Allow = "Allow",
        ResetCodexThread = "New Codex session",
        ResetCodexThreadTitle = "Start a new Codex session?",
        ResetCodexThreadMessage = "The next task will start a new Codex App Server thread for this Workstream. Existing task data will be kept.",
        CodexThreadReset = "The Codex session will restart with the next task.",
        AddUserNote = "Optional note for Codex",
        ResultNote = "Optional note before returning the result",
        SendToChatGPT = "Send back to ChatGPT",
        RetrySendToChatGPT = "Retry sending to ChatGPT",
        CompleteCurrentRound = "Complete current round",
        ClearCurrentTask = "Clear current task",
        ClearCurrentTaskTitle = "Clear current task?",
        ClearCurrentTaskMessage = "Clear current task?\n\nThis will clear the current Prompt, Result, and unfinished delivery state, then return this Workstream to waiting for a new task.\n\nIt will not delete the Project, Workstream, or ChatGPT tab binding.",
        CurrentRoundCompleted = "Current round completed.",
        CurrentTaskCleared = "Current task cleared. The Workstream is ready for a new task.",
        CodexSimulationStarted = "Simulation started. Use ⋯ → Simulate Codex result.",
        HandoffQueued = "Sending result to the original ChatGPT tab...",
        HandoffFailed = "Could not fill ChatGPT. You can retry; the result is preserved.",
        HandoffFallback = "BlueRelay could not fill ChatGPT automatically; the result was copied to the clipboard.",
        HandoffDelivered = "Result inserted into ChatGPT.",
        SimulatedResultSaved = "Simulated result saved. It is ready for ChatGPT.",
        Debug = "Debug",
        ManualState = "Set state manually",
        ApplyDebugState = "Apply debug state",
        SimulateCodexResult = "Simulate Codex result",
        SimulatedResultTitle = "Simulated Codex result",
        SimulatedResultHint = "This developer-only result is not sent to a real Codex session.",
        ResetPairingTitle = "Reset browser pairing?",
        ResetPairingMessage = "Existing extension tokens will stop working. Generate a new pairing code afterward.",
        PairingReset = "Browser pairing reset.",
        WorkstreamUnbound = "ChatGPT tab unbound.",
        UnbindWorkstream = "Unbind ChatGPT tab",
        PairingCodeLabel = "Pairing code",
        StateLabels = new Dictionary<WorkflowState, string>
        {
            [WorkflowState.Idle] = "Waiting for a new task",
            [WorkflowState.ReadyForCodex] = "Next: send to Codex",
            [WorkflowState.CodexRunning] = "Codex is running",
            [WorkflowState.ReadyForChatGPT] = "Next: send to ChatGPT",
            [WorkflowState.ChatGPTReviewing] = "Waiting for ChatGPT review",
            [WorkflowState.Completed] = "Round completed",
            [WorkflowState.NeedsAttention] = "Needs attention",
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
            [WorkflowState.NeedsAttention] = "Review the issue, then retry or clear the task.",
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
        Settings = "项目管理",
        Workflow = "工作流",
        WorkflowSubtitle = "查看当前进度和下一位执行者。",
        NextAction = "下一步",
        Pending = "待处理",
        CurrentTask = "当前任务",
        TaskPrompt = "任务提示",
        TaskResult = "结果",
        CurrentTaskNone = "未分配任务",
        Projects = "工作流",
        Workstream = "工作流",
        DefaultWorkstream = "默认工作流",
        WorkstreamManagement = "工作流管理",
        WorkstreamCountLabel = "个工作流",
        NoSession = "暂无会话绑定",
        DebugState = "调试状态",
        NewWorkstream = "新建工作流",
        RenameWorkstream = "重命名",
        DeleteWorkstream = "删除",
        WorkstreamName = "工作流名称",
        DeleteWorkstreamTitle = "删除工作流？",
        DeleteWorkstreamMessageFormat = "要从此项目删除“{0}”吗？",
        LastWorkstreamRequired = "每个项目至少要保留一个工作流。",
        WorkstreamCreated = "工作流已创建。",
        WorkstreamUpdated = "工作流已更新。",
        WorkstreamDeleted = "工作流已删除。",
        ProjectManagement = "项目管理",
        ProjectManagementSubtitle = "管理项目和工作流记录，不会修改本地文件。",
        AddProject = "添加项目",
        NewProject = "新建项目",
        EmptyTitle = "还没有工作流",
        EmptyDescription = "添加项目后，这里会显示 ChatGPT ↔ Codex 的接力状态。",
        NoProjects = "还没有项目。",
        ProjectDetails = "项目详情",
        Name = "名称",
        LocalPath = "本地路径",
        Browse = "浏览",
        Edit = "编辑",
        Delete = "删除",
        Cancel = "取消",
        Replace = "替换",
        SaveProject = "保存项目",
        SaveWorkstream = "保存工作流",
        Close = "关闭",
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
        ChooseProjectDirectory = "选择本地项目目录。",
        GitDetected = "检测到 Git 仓库",
        GitNotDetected = "未检测到 Git 仓库",
        GitUnavailable = "未检测到 Git",
        GitRepositoryHint = "已根据 Git 填写仓库根目录和项目名称。",
        GitNotRepositoryHint = "未找到 Git 仓库，将使用所选文件夹。",
        GitUnavailableHint = "未检测到 Git，将使用所选文件夹名称。",
        RepositoryRoot = "仓库根目录",
        SelectedFolder = "所选文件夹",
        UseRepositoryRoot = "使用仓库根目录",
        RefreshGit = "刷新 Git 信息",
        BrowserBridge = "浏览器连接",
        CodexAppServer = "Codex App Server",
        BrowserBridgeRunning = "浏览器连接已启动",
        BrowserBridgeUnavailable = "浏览器连接不可用",
        BridgeEndpoint = "本地地址",
        PairingCodeNotGenerated = "生成配对码",
        GeneratePairingCode = "生成配对码",
        ResetPairing = "重置配对",
        PairingInstructions = "在扩展弹窗中输入此一次性配对码，有效期五分钟。",
        BrowserConnected = "ChatGPT · 已连接",
        BrowserDisconnected = "ChatGPT · 已断开",
        BrowserNotBound = "ChatGPT · 未绑定",
        ChatGPTBinding = "ChatGPT",
        ChatGPTNotBound = "未绑定",
        ChatGPTConversationChanged = "对话已切换，需要重新绑定",
        CodexSession = "Codex",
        CodexNotBound = "未绑定 Codex 会话",
        CodexBound = "会话已绑定",
        CodexConflict = "会话正被其他进程使用",
        NewCodexSessionAndRetry = "新建 Codex 会话并重试",
        NewCodexSessionAndRetryTitle = "新建 Codex 会话并重试？",
        NewCodexSessionAndRetryMessage = "将保留当前任务和 ChatGPT 绑定，只替换 Codex 会话，然后重试当前任务。",
        CodexSessionRetried = "已新建 Codex 会话并重试当前任务。",
        CopyCodexThreadId = "复制 Codex 线程 ID",
        CodexThreadIdCopied = "Codex 线程 ID 已复制。",
        SendToCodex = "确认发送",
        FillCodex = "填入 Codex",
        CodexComposerFilled = "已填入 Codex，等待你检查并发送。",
        CodexComposerFilledClipboardRestoreFailed = "已填入 Codex，但原纯文本剪贴板未能恢复。",
        CodexComposerFilledClipboardWarning = "已填入 Codex，但原剪贴板内容未能完整保存或恢复。",
        CodexComposerClipboardReferenceAccepted = "长文本已作为 Codex 粘贴引用填入，请检查后发送。",
        CodexComposerClipboardReferenceAcceptedRestoreFailed = "长文本已作为 Codex 粘贴引用填入，但原纯文本剪贴板未能恢复。",
        CodexComposerClipboardRestoreFailed = "填入 Codex 未完成，且原纯文本剪贴板未能恢复。",
        CodexComposerVerificationFailed = "Codex 输入框未能完整写入任务内容，请重试。",
        CodexComposerNotFound = "没有找到可编辑的 Codex 输入框。\n请先打开 Codex 会话后重试。",
        CodexComposerClipboardUnsafe = "无法安全使用剪贴板填入 Codex。请关闭其他剪贴板内容后重试。",
        CodexComposerClipboardWriteFailed = "无法确认任务文本已写入剪贴板，请重试。",
        CodexComposerTaskEmpty = "当前任务为空，无法填入 Codex。",
        CodexComposerInjectionFailed = "无法填入 Codex，请重试。",
        CodexComposerSearching = "正在查找 Codex 输入框…",
        CodexComposerProbeTimeout = "查找 Codex 输入框超时，请确认 Codex 会话已打开后重试。",
        CodexComposerBusy = "已有一次 Codex 输入操作正在进行，请稍候再试。",
        CodexComposerCancelled = "已取消填入 Codex。",
        CodexComposerForegroundFailed = "无法将 Codex 激活为前台窗口，请先将 Codex 切到前台后重试。",
        CodexComposerExistingTextTitle = "替换 Codex 中已有文字？",
        CodexComposerExistingTextMessage = "Codex 输入框中已有文字。要用当前任务替换它吗？现有文字会被覆盖。",
        CodexComposerContentUnknownTitle = "确认替换 Codex 输入框内容",
        CodexComposerContentUnknownMessage = "BlueRelay 无法安全确认 Codex 输入框是否为空。只有确定要替换时才继续。",
        FocusedComposerProbeTitle = "焦点 Composer UIA 诊断",
        FocusedComposerProbeInstructions = "请先点击 Codex 输入框，再在不点击 BlueRelay 的情况下按全局热键。",
        FocusedComposerProbeHotkey = "热键：Ctrl + Alt + B",
        FocusedComposerProbePrepare = "准备诊断 Codex 输入框",
        FocusedComposerProbeRunning = "正在读取当前焦点的 Codex UI Automation 元数据…",
        FocusedComposerProbeCompleted = "当前焦点元素属于 Codex Desktop。",
        FocusedComposerProbeNotCodex = "当前焦点不在 Codex Desktop。",
        FocusedComposerProbeUnavailable = "当前没有可用的焦点 UI Automation 元素。",
        FocusedComposerProbeTimeout = "焦点 Composer 诊断超时，请保持 Codex 输入框获得焦点后重试。",
        FocusedComposerProbeBusy = "已有一次焦点 Composer 诊断正在进行。",
        FocusedComposerProbeCancelled = "已取消焦点 Composer 诊断。",
        FocusedComposerProbeFailed = "焦点 Composer 诊断失败，请重试。",
        RetryCodex = "重试发送给 Codex",
        CodexRunning = "Codex 运行中",
        CodexCancel = "取消 Codex 任务",
        CodexCancelled = "Codex 任务已取消。",
        CodexProgress = "运行进度",
        CodexApprovalTitle = "Codex 请求审批",
        CodexApprovalMessage = "Codex 请求继续执行。请查看请求后决定是否允许。",
        Allow = "允许",
        ResetCodexThread = "新建 Codex 会话",
        ResetCodexThreadTitle = "新建 Codex 会话？",
        ResetCodexThreadMessage = "该工作流的下一次任务会在 Codex App Server 中新建线程。已有任务数据会保留。",
        CodexThreadReset = "下一次任务将使用新的 Codex 会话。",
        AddUserNote = "给 Codex 的补充说明（可选）",
        ResultNote = "返回结果前的补充说明（可选）",
        SendToChatGPT = "发回 ChatGPT",
        RetrySendToChatGPT = "重试返回 ChatGPT",
        CompleteCurrentRound = "结束本轮",
        ClearCurrentTask = "清空当前任务",
        ClearCurrentTaskTitle = "清空当前任务？",
        ClearCurrentTaskMessage = "清空当前任务？\n\n将清除当前 Prompt、Result 和未完成的投递状态，并让该工作流恢复为等待新任务。\n\n不会删除项目、工作流或 ChatGPT 标签页绑定。",
        CurrentRoundCompleted = "本轮已结束。",
        CurrentTaskCleared = "当前任务已清空，工作流正在等待新任务。",
        CodexSimulationStarted = "已进入模拟运行，请使用 ⋯ → 模拟 Codex 返回。",
        HandoffQueued = "正在发送到原 ChatGPT 标签页…",
        HandoffFailed = "未能填入 ChatGPT，可重试。结果仍已保留。",
        HandoffFallback = "BlueRelay 无法自动填入，结果已复制到剪贴板。",
        HandoffDelivered = "结果已填入 ChatGPT。",
        SimulatedResultSaved = "模拟结果已保存，可以发回 ChatGPT。",
        Debug = "调试",
        ManualState = "手动设置状态",
        ApplyDebugState = "应用调试状态",
        SimulateCodexResult = "模拟 Codex 返回",
        SimulatedResultTitle = "模拟 Codex 返回",
        SimulatedResultHint = "这里只用于开发测试，不会连接真实 Codex 会话。",
        ResetPairingTitle = "重置浏览器配对？",
        ResetPairingMessage = "现有扩展 token 将失效，之后需要重新生成配对码。",
        PairingReset = "浏览器配对已重置。",
        WorkstreamUnbound = "ChatGPT 标签页已解除绑定。",
        UnbindWorkstream = "解除 ChatGPT 绑定",
        PairingCodeLabel = "配对码",
        StateLabels = new Dictionary<WorkflowState, string>
        {
            [WorkflowState.Idle] = "等待新任务",
            [WorkflowState.ReadyForCodex] = "下一步：发送给 Codex",
            [WorkflowState.CodexRunning] = "Codex 运行中",
            [WorkflowState.ReadyForChatGPT] = "下一步：发送给 ChatGPT",
            [WorkflowState.ChatGPTReviewing] = "等待 ChatGPT 审查",
            [WorkflowState.Completed] = "本轮已完成",
            [WorkflowState.NeedsAttention] = "需要处理",
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
            [WorkflowState.NeedsAttention] = "检查问题后重试，或清空任务。",
            [WorkflowState.Error] = "处理问题后再继续。"
        }
    };
}
