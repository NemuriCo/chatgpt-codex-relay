using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace BlueRelay;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    public void CloseFromApplication()
    {
        _allowClose = true;
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 20;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
            SnapToScreenEdge();
        }
        catch (InvalidOperationException)
        {
            // DragMove can be interrupted while the window is being hidden.
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
    }

    private void SnapToScreenEdge()
    {
        var workArea = SystemParameters.WorkArea;
        const double snapDistance = 24;

        if (Math.Abs(Left - workArea.Left) <= snapDistance)
        {
            Left = workArea.Left;
        }
        else if (Math.Abs(workArea.Right - (Left + ActualWidth)) <= snapDistance)
        {
            Left = workArea.Right - ActualWidth;
        }

        if (Math.Abs(Top - workArea.Top) <= snapDistance)
        {
            Top = workArea.Top;
        }
        else if (Math.Abs(workArea.Bottom - (Top + ActualHeight)) <= snapDistance)
        {
            Top = workArea.Bottom - ActualHeight;
        }
    }
}
