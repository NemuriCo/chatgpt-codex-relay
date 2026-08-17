namespace BlueRelay.Models;

/// <summary>
/// Runtime identity and last-known state for one browser tab binding.
/// The tab key is installation-scoped and is never derived from a page title.
/// </summary>
public sealed class BrowserBinding
{
    public string InstallationId { get; set; } = string.Empty;

    public string TabId { get; set; } = string.Empty;

    public string ChatGPTUrl { get; set; } = string.Empty;

    public string? ChatGPTConversationId { get; set; }

    public string PageTitle { get; set; } = string.Empty;

    public Guid? WorkstreamId { get; set; }

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public bool Connected { get; set; }

    public string TabKey => CreateTabKey(InstallationId, TabId);

    public static string CreateTabKey(string installationId, string tabId)
    {
        return $"{installationId}:{tabId}";
    }
}
