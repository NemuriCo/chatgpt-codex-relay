namespace BlueRelay.Services.Dialogs;

public interface IDialogService
{
    bool Confirm(string title, string message);

    void ShowError(string title, string message);
}
