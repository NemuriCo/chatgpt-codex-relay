using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace BlueRelay.Services.Dialogs;

public sealed class MessageBoxDialogService : IDialogService
{
    public bool Confirm(string title, string message)
    {
        return WpfMessageBox.Show(message, title, WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Warning) == WpfMessageBoxResult.Yes;
    }

    public void ShowError(string title, string message)
    {
        WpfMessageBox.Show(message, title, WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
    }
}
