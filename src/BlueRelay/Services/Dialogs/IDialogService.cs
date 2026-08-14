namespace BlueRelay.Services.Dialogs;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);

    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default);
}
