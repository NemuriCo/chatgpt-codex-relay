using System.Text.Json.Serialization;

namespace BlueRelay.Models;

public sealed class Project
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Workstream> Workstreams { get; set; } = [];

    // These nullable compatibility fields are consumed by StateMigration and omitted after migration.
    [JsonPropertyName("CurrentState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowState? LegacyCurrentState { get; set; }

    [JsonPropertyName("ChatGPTTab")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyChatGPTTab { get; set; }

    [JsonPropertyName("CodexSessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCodexSessionId { get; set; }

    [JsonPropertyName("CurrentTaskId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCurrentTaskId { get; set; }
}
