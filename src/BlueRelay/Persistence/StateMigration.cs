using BlueRelay.Models;

namespace BlueRelay.Persistence;

public static class StateMigration
{
    public const int CurrentSchemaVersion = 2;

    public static bool Migrate(ApplicationState state)
    {
        var changed = state.SchemaVersion < CurrentSchemaVersion;
        state.Projects ??= [];

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
