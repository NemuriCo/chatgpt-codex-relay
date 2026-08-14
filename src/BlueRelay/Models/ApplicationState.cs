namespace BlueRelay.Models;

public sealed class ApplicationState
{
    public int SchemaVersion { get; set; } = 1;

    public List<Project> Projects { get; set; } = [];

    public Guid? SelectedProjectId { get; set; }

    public bool IsAlwaysOnTop { get; set; } = true;
}
