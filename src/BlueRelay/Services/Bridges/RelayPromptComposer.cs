namespace BlueRelay.Services.Bridges;

public static class RelayPromptComposer
{
    public static string Compose(string? userNote, string payload)
    {
        var note = string.IsNullOrWhiteSpace(userNote) ? null : userNote.Trim();
        var body = payload ?? string.Empty;
        if (note is null)
        {
            return body;
        }

        return $"用户补充：\n{note}\n\n完整任务：\n{body}";
    }

    public static string ComposeResult(string? resultNote, string result)
    {
        var note = string.IsNullOrWhiteSpace(resultNote) ? null : resultNote.Trim();
        var body = result ?? string.Empty;
        if (note is null)
        {
            return $"Codex 执行结果：\n{body}";
        }

        return $"用户补充：\n{note}\n\nCodex 执行结果：\n{body}";
    }
}
