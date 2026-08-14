namespace BlueRelay.Models;

public sealed class ApplicationState
{
    public int SchemaVersion { get; set; } = 2;

    public List<Project> Projects { get; set; } = [];

    public Guid? SelectedProjectId { get; set; }

    public bool IsAlwaysOnTop { get; set; } = true;

    // Nullable coordinates keep older state.json files readable and allow a safe default on first launch.
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool IsWindowCollapsed { get; set; }
}
