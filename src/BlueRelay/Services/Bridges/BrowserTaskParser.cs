namespace BlueRelay.Services.Bridges;

public static class BrowserTaskParser
{
    public const string Marker = "# CODEX_TASK";

    public static bool TryNormalize(string? text, out string prompt)
    {
        prompt = NormalizeTaskText(text);
        return prompt.Contains(Marker, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeTaskText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    public static bool AreEquivalentPayloads(string? first, string? second)
    {
        return string.Equals(
            NormalizeTaskText(first),
            NormalizeTaskText(second),
            StringComparison.Ordinal);
    }
}
