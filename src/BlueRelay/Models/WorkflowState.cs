namespace BlueRelay.Models;

public enum WorkflowState
{
    Idle,
    ReadyForCodex,
    CodexRunning,
    ReadyForChatGPT,
    ChatGPTReviewing,
    Completed,
    Error
}
