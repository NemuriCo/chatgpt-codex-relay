namespace BlueRelay.Models;

public sealed class ApplicationState
{
    public int SchemaVersion { get; set; } = 3;

    public List<Project> Projects { get; set; } = [];

    public Guid? SelectedProjectId { get; set; }

    public bool IsAlwaysOnTop { get; set; } = true;

    // Nullable coordinates keep older state.json files readable and allow a safe default on first launch.
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    // Nullable dimensions keep older state.json files compatible and allow the window to use its defaults.
    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool IsWindowCollapsed { get; set; }

    public string? CodexExecutablePath { get; set; }

    public BrowserBridgeState BrowserBridge { get; set; } = new();
}
