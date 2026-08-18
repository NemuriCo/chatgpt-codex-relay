using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace BlueRelay.Services.Dialogs;

public sealed record AskDialogButtonConfiguration(
    string PrimaryButtonText,
    string? SecondaryButtonText,
    bool IsSecondaryButtonEnabled,
    string CloseButtonText,
    bool IsCloseButtonEnabled)
{
    public int VisibleActionButtonCount =>
        (string.IsNullOrWhiteSpace(PrimaryButtonText) ? 0 : 1) +
        (IsSecondaryButtonEnabled && !string.IsNullOrWhiteSpace(SecondaryButtonText) ? 1 : 0) +
        (IsCloseButtonEnabled && !string.IsNullOrWhiteSpace(CloseButtonText) ? 1 : 0);

    public static AskDialogButtonConfiguration ReplaceOrCancel(
        string replaceLabel,
        string cancelLabel) =>
        new(
            replaceLabel,
            SecondaryButtonText: null,
            IsSecondaryButtonEnabled: false,
            cancelLabel,
            IsCloseButtonEnabled: true);

    public static bool IsAccepted(WpfUiMessageBoxResult result) =>
        result == WpfUiMessageBoxResult.Primary;
}
