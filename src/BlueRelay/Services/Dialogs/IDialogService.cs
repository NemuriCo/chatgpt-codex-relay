namespace BlueRelay.Services.Dialogs;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);

    Task<bool> AskAsync(
        string title,
        string message,
        string acceptLabel,
        CancellationToken cancellationToken = default)
    {
        return ConfirmAsync(title, message, cancellationToken);
    }

    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default);
}
