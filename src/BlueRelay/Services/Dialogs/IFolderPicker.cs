namespace BlueRelay.Services.Dialogs;

public interface IFolderPicker
{
    string? Pick(string? initialPath);
}
