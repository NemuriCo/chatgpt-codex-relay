using Forms = System.Windows.Forms;
using BlueRelay.Localization;

namespace BlueRelay.Services.Dialogs;

public sealed class WindowsFolderPicker : IFolderPicker
{
    private readonly UiTextSet _text;

    public WindowsFolderPicker(UiTextSet? text = null)
    {
        _text = text ?? LocalizationService.Current;
    }

    public string? Pick(string? initialPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _text.ChooseProjectDirectory,
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
