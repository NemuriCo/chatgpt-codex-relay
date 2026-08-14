using Forms = System.Windows.Forms;

namespace BlueRelay.Services.Dialogs;

public sealed class WindowsFolderPicker : IFolderPicker
{
    public string? Pick(string? initialPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the local project directory.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.SelectedPath = initialPath;
        }

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
