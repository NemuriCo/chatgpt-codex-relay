namespace BlueRelay.Models;

public sealed class BrowserBridgeState
{
    /// <summary>
    /// Long-lived random token issued during pairing. It is stored only in the
    /// user's local state file and is never written to diagnostics.
    /// </summary>
    public string? AuthToken { get; set; }

    public List<string> PairedInstallationIds { get; set; } = [];

    public List<BrowserBinding> Bindings { get; set; } = [];

    public List<RelayTask> Tasks { get; set; } = [];
}
