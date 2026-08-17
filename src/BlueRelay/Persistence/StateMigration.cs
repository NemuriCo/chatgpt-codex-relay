using BlueRelay.Models;

namespace BlueRelay.Persistence;

public static class StateMigration
{
    public const int CurrentSchemaVersion = 4;

    public static bool Migrate(ApplicationState state)
    {
        var changed = state.SchemaVersion < CurrentSchemaVersion;
        state.Projects ??= [];
        state.BrowserBridge ??= new BrowserBridgeState();
        state.BrowserBridge.PairedInstallationIds ??= [];
        state.BrowserBridge.Bindings ??= [];
        state.BrowserBridge.Tasks ??= [];

        foreach (var project in state.Projects)
        {
            project.Workstreams ??= [];
            foreach (var workstream in project.Workstreams)
            {
                if (workstream.ProjectId == Guid.Empty)
                {
                    workstream.ProjectId = project.Id;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(workstream.CodexThreadId) &&
                    !string.IsNullOrWhiteSpace(workstream.CodexSessionId))
                {
                    workstream.CodexThreadId = workstream.CodexSessionId;
                    changed = true;
                }

                var binding = state.BrowserBridge.Bindings.FirstOrDefault(item => item.WorkstreamId == workstream.Id);
                if (binding is not null)
                {
                    if (string.IsNullOrWhiteSpace(workstream.BrowserInstallationId))
                    {
                        workstream.BrowserInstallationId = binding.InstallationId;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(workstream.ChatGPTTabId))
                    {
                        workstream.ChatGPTTabId = binding.TabId;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId) &&
                        !string.IsNullOrWhiteSpace(binding.ChatGPTConversationId))
                    {
                        workstream.ChatGPTConversationId = binding.ChatGPTConversationId;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(workstream.ChatGPTUrl) && !string.IsNullOrWhiteSpace(binding.ChatGPTUrl))
                    {
                        workstream.ChatGPTUrl = binding.ChatGPTUrl;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(workstream.ChatGPTTitle) && !string.IsNullOrWhiteSpace(binding.PageTitle))
                    {
                        workstream.ChatGPTTitle = binding.PageTitle;
                        changed = true;
                    }

                    var mismatch = !string.IsNullOrWhiteSpace(workstream.ChatGPTConversationId) &&
                                   !string.Equals(workstream.ChatGPTConversationId, binding.ChatGPTConversationId, StringComparison.Ordinal);
                    if (binding.ConversationMismatch != mismatch)
                    {
                        binding.ConversationMismatch = mismatch;
                        changed = true;
                    }
                }
            }

            if (project.Workstreams.Count == 0)
            {
                var now = project.UpdatedAt == default ? DateTimeOffset.UtcNow : project.UpdatedAt;
                project.Workstreams.Add(new Workstream
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Name = Workstream.DefaultName,
                    CurrentState = project.LegacyCurrentState ?? WorkflowState.Idle,
                    CreatedAt = project.CreatedAt == default ? now : project.CreatedAt,
                    UpdatedAt = now,
                    ChatGPTTabId = project.LegacyChatGPTTab,
                    CodexSessionId = project.LegacyCodexSessionId,
                    CodexThreadId = project.LegacyCodexSessionId,
                    CurrentTaskId = project.LegacyCurrentTaskId
                });
                changed = true;
            }

            if (project.LegacyCurrentState is not null ||
                project.LegacyChatGPTTab is not null ||
                project.LegacyCodexSessionId is not null ||
                project.LegacyCurrentTaskId is not null)
            {
                project.LegacyCurrentState = null;
                project.LegacyChatGPTTab = null;
                project.LegacyCodexSessionId = null;
                project.LegacyCurrentTaskId = null;
                changed = true;
            }
        }

        if (state.SchemaVersion != CurrentSchemaVersion)
        {
            state.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }
}
