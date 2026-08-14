using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BlueRelay.Diagnostics;
using BlueRelay.Presentation.ViewModels;
using FormsScreen = System.Windows.Forms.Screen;

namespace BlueRelay;

public partial class MainWindow : Window
{
    private const double SnapDistance = 20;
    private const double CollapsedHeight = 50;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;
    private bool _allowClose;
    private bool _isRestoringPosition;
    private MainViewModel? _viewModel;

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

    public async Task SaveWindowSettingsAsync()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateWindowPosition(Left, Top);
        await viewModel.SaveWindowSettingsAsync();
    }

    public void CloseFromApplication()
    {
        _allowClose = true;
        Close();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var preference = DwmCornerRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _isRestoringPosition = true;
            try
            {
                ApplyCollapsedLayout(viewModel.IsCollapsed);
                UpdateLayout();
                if (viewModel.WindowLeft.HasValue && viewModel.WindowTop.HasValue && IsPositionVisible(viewModel.WindowLeft.Value, viewModel.WindowTop.Value))
                {
                    Left = viewModel.WindowLeft.Value;
                    Top = viewModel.WindowTop.Value;
                }
                else
                {
                    SetDefaultPosition();
                }

                viewModel.UpdateWindowPosition(Left, Top);
            }
            finally
            {
                _isRestoringPosition = false;
            }
        }

        StartupDiagnostics.Write($"MainWindow Loaded IsVisible={IsVisible} Bounds={Left},{Top},{ActualWidth},{ActualHeight}");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCollapsed) && _viewModel is not null)
        {
            ApplyCollapsedLayout(_viewModel.IsCollapsed);
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (!_isRestoringPosition && DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateWindowPosition(Left, Top);
        }
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SnapToScreenEdge();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        StartupDiagnostics.Write("MainWindow Closing intercepted; hiding to tray");
        e.Cancel = true;
        HideToTray();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void HideToTray()
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateWindowPosition(Left, Top);
            _ = viewModel.SaveWindowSettingsAsync();
        }

        Hide();
    }

    private void SetDefaultPosition()
    {
        var workArea = GetCurrentWorkingArea();
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - ActualHeight - 18;
    }

    private void ApplyCollapsedLayout(bool isCollapsed)
    {
        if (isCollapsed)
        {
            SizeToContent = SizeToContent.Manual;
            Height = CollapsedHeight;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
        }
    }

    private void SnapToScreenEdge()
    {
        var workArea = GetCurrentWorkingArea();
        if (Math.Abs(Left - workArea.Left) <= SnapDistance)
        {
            Left = workArea.Left;
        }
        else if (Math.Abs(workArea.Right - (Left + ActualWidth)) <= SnapDistance)
        {
            Left = workArea.Right - ActualWidth;
        }

        if (Math.Abs(Top - workArea.Top) <= SnapDistance)
        {
            Top = workArea.Top;
        }
        else if (Math.Abs(workArea.Bottom - (Top + ActualHeight)) <= SnapDistance)
        {
            Top = workArea.Bottom - ActualHeight;
        }

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateWindowPosition(Left, Top);
            _ = viewModel.SaveWindowSettingsAsync();
        }
    }

    private Rect GetCurrentWorkingArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero
            ? FormsScreen.PrimaryScreen!
            : FormsScreen.FromHandle(handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = 96 / dpi.PixelsPerInchX;
        var scaleY = 96 / dpi.PixelsPerInchY;
        var area = screen.WorkingArea;
        return new Rect(area.Left * scaleX, area.Top * scaleY, area.Width * scaleX, area.Height * scaleY);
    }

    private bool IsPositionVisible(double left, double top)
    {
        var right = left + Math.Max(Width, 1);
        var bottom = top + Math.Max(ActualHeight, 1);
        foreach (var screen in FormsScreen.AllScreens)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = 96 / dpi.PixelsPerInchX;
            var scaleY = 96 / dpi.PixelsPerInchY;
            var area = screen.WorkingArea;
            var workArea = new Rect(area.Left * scaleX, area.Top * scaleY, area.Width * scaleX, area.Height * scaleY);
            if (new Rect(left, top, right - left, bottom - top).IntersectsWith(workArea))
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
