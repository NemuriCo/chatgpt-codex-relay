using System.Windows;
using BlueRelay.Localization;
using Wpf.Ui.Controls;
using WpfUiApplication = System.Windows.Application;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace BlueRelay.Services.Dialogs;

public sealed class MessageBoxDialogService : IDialogService
{
    private readonly UiTextSet _text;

    public MessageBoxDialogService()
        : this(LocalizationService.Current)
    {
    }

    public MessageBoxDialogService(UiTextSet text)
    {
        _text = text;
    }

    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var messageBox = new WpfUiMessageBox
        {
            Owner = GetOwner(),
            Title = title,
            Content = message,
            PrimaryButtonText = _text.Delete,
            PrimaryButtonAppearance = ControlAppearance.Danger,
            PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Delete16),
            SecondaryButtonText = _text.Cancel,
            CloseButtonText = _text.Cancel,
            ShowTitle = true
        };

        var result = await messageBox.ShowDialogAsync(cancellationToken: cancellationToken);
        return result == WpfUiMessageBoxResult.Primary;
    }

    public async Task<bool> AskAsync(
        string title,
        string message,
        string acceptLabel,
        CancellationToken cancellationToken = default)
    {
        var messageBox = new WpfUiMessageBox
        {
            Owner = GetOwner(),
            Title = title,
            Content = message,
            PrimaryButtonText = acceptLabel,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Checkmark16),
            SecondaryButtonText = _text.Cancel,
            CloseButtonText = _text.Cancel,
            ShowTitle = true
        };

        var result = await messageBox.ShowDialogAsync(cancellationToken: cancellationToken);
        return result == WpfUiMessageBoxResult.Primary;
    }

    public async Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var messageBox = new WpfUiMessageBox
        {
            Owner = GetOwner(),
            Title = title,
            Content = message,
            PrimaryButtonText = _text.Close,
            PrimaryButtonAppearance = ControlAppearance.Danger,
            PrimaryButtonIcon = new SymbolIcon(SymbolRegular.ErrorCircle12),
            ShowTitle = true
        };

        await messageBox.ShowDialogAsync(cancellationToken: cancellationToken);
    }

    private static Window? GetOwner()
    {
        return WpfUiApplication.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? WpfUiApplication.Current?.MainWindow;
    }
}
