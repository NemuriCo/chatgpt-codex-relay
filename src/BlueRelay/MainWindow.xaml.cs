using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BlueRelay.Diagnostics;
using BlueRelay.Presentation.ViewModels;
using FormsScreen = System.Windows.Forms.Screen;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace BlueRelay;

public partial class MainWindow : Window
{
    private const double SnapDistance = 20;
    private const double CollapsedHeight = 50;
    private const double DefaultWindowWidth = 378;
    private const double DefaultWindowHeight = 320;
    private const double MinimumExpandedHeight = 180;
    private const double MaximumWindowWidth = 800;
    private const double MaximumWindowHeight = 900;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;
    private const int FocusedComposerProbeHotKeyId = 0x4252;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyB = 0x42;
    private bool _allowClose;
    private bool _isRestoringPosition;
    private bool _focusedComposerProbeHotKeyRegistered;
    private MainViewModel? _viewModel;
    private HwndSource? _windowSource;
    private readonly DispatcherTimer _windowSettingsSaveTimer;

    public MainWindow()
    {
        InitializeComponent();
        _windowSettingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _windowSettingsSaveTimer.Tick += WindowSettingsSaveTimer_Tick;
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
        viewModel.UpdateWindowSize(Width, Height);
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
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        _focusedComposerProbeHotKeyRegistered = RegisterHotKey(
            handle,
            FocusedComposerProbeHotKeyId,
            ModControl | ModAlt | ModNoRepeat,
            VirtualKeyB);
        if (!_focusedComposerProbeHotKeyRegistered)
        {
            StartupDiagnostics.Write($"Focused composer probe hotkey registration failed error={Marshal.GetLastWin32Error()}");
        }

        var preference = DwmCornerRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    protected override void OnClosed(EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_focusedComposerProbeHotKeyRegistered)
        {
            UnregisterHotKey(handle, FocusedComposerProbeHotKeyId);
            _focusedComposerProbeHotKeyRegistered = false;
        }

        _windowSource?.RemoveHook(WindowMessageHook);
        base.OnClosed(e);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt64() == FocusedComposerProbeHotKeyId)
        {
            _ = _viewModel?.RunFocusedComposerProbeAsync();
            handled = true;
        }

        return IntPtr.Zero;
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
                ApplyWindowSizeLimits(GetCurrentWorkingArea());
                ApplyCollapsedLayout(viewModel.IsCollapsed);
                UpdateLayout();
                if (viewModel.WindowLeft.HasValue && viewModel.WindowTop.HasValue)
                {
                    Left = viewModel.WindowLeft.Value;
                    Top = viewModel.WindowTop.Value;
                }
                else
                {
                    SetDefaultPosition();
                }

                ApplyWindowSizeLimits(GetWorkingAreaForBounds(Left, Top, ActualWidth, ActualHeight));
                RestoreExpandedSize(viewModel);
                ClampWindowToWorkingArea();
                viewModel.UpdateWindowPosition(Left, Top);
                viewModel.UpdateWindowSize(Width, Height);
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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isRestoringPosition || _viewModel is null || _viewModel.IsCollapsed)
        {
            return;
        }

        _viewModel.UpdateWindowSize(Width, Height);
        ScheduleWindowSettingsSave();
    }

    private void ScheduleWindowSettingsSave()
    {
        _windowSettingsSaveTimer.Stop();
        _windowSettingsSaveTimer.Start();
    }

    private async void WindowSettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _windowSettingsSaveTimer.Stop();
        await SaveWindowSettingsAsync();
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

    private void OpenContextMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is not null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void ProjectCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement element && element.DataContext is Models.Project project)
        {
            viewModel.SelectProjectCommand.Execute(project);
        }
    }

    private void WorkstreamCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement element && element.DataContext is ProjectListItemViewModel workstream)
        {
            viewModel.SelectWorkstreamCommand.Execute(workstream);
        }
    }

    private void HideToTray()
    {
        _ = SaveWindowSettingsAsync();
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
        var wasRestoringPosition = _isRestoringPosition;
        _isRestoringPosition = true;
        try
        {
            if (isCollapsed)
            {
                SizeToContent = SizeToContent.Manual;
                ResizeMode = ResizeMode.NoResize;
                MinHeight = 0;
                Height = CollapsedHeight;
            }
            else
            {
                SizeToContent = SizeToContent.Manual;
                ResizeMode = ResizeMode.CanResize;
                MinHeight = MinimumExpandedHeight;
                if (_viewModel is not null)
                {
                    RestoreExpandedSize(_viewModel);
                }
            }
        }
        finally
        {
            _isRestoringPosition = wasRestoringPosition;
        }
    }

    private void ApplyWindowSizeLimits(Rect workArea)
    {
        MinWidth = Math.Min(360, workArea.Width);
        MaxWidth = Math.Max(MinWidth, Math.Min(MaximumWindowWidth, workArea.Width));
        MaxHeight = Math.Max(MinimumExpandedHeight, Math.Min(MaximumWindowHeight, workArea.Height));
    }

    private void RestoreExpandedSize(MainViewModel viewModel)
    {
        if (viewModel.IsCollapsed)
        {
            return;
        }

        Width = Clamp(viewModel.WindowWidth ?? DefaultWindowWidth, MinWidth, MaxWidth);
        Height = Clamp(viewModel.WindowHeight ?? DefaultWindowHeight, MinHeight, MaxHeight);
    }

    private void ClampWindowToWorkingArea()
    {
        var workArea = GetWorkingAreaForBounds(Left, Top, ActualWidth, ActualHeight);
        if (!IsCollapsedWindow() && ActualWidth > workArea.Width)
        {
            Width = workArea.Width;
        }

        if (!IsCollapsedWindow() && ActualHeight > workArea.Height)
        {
            Height = workArea.Height;
        }

        Left = Math.Clamp(Left, workArea.Left, workArea.Right - ActualWidth);
        Top = Math.Clamp(Top, workArea.Top, workArea.Bottom - ActualHeight);
    }

    private Rect GetWorkingAreaForBounds(double left, double top, double width, double height)
    {
        var bounds = new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1));
        Rect? bestArea = null;
        var bestIntersection = 0d;
        foreach (var workArea in GetWorkingAreas())
        {
            var intersection = Rect.Intersect(bounds, workArea);
            var area = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
            if (area > bestIntersection)
            {
                bestArea = workArea;
                bestIntersection = area;
            }
        }

        return bestArea ?? GetCurrentWorkingArea();
    }

    private IEnumerable<Rect> GetWorkingAreas()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = 96 / dpi.PixelsPerInchX;
        var scaleY = 96 / dpi.PixelsPerInchY;
        foreach (var screen in FormsScreen.AllScreens)
        {
            var area = screen.WorkingArea;
            yield return new Rect(area.Left * scaleX, area.Top * scaleY, area.Width * scaleX, area.Height * scaleY);
        }
    }

    private bool IsCollapsedWindow()
    {
        return _viewModel?.IsCollapsed == true;
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        return Math.Max(minimum, Math.Min(maximum, value));
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

    private void TaskTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfTextBox textBox || FindVisualChild<ScrollViewer>(textBox) is not { } scrollViewer)
        {
            return;
        }

        var scrollingUpAtTop = e.Delta > 0 && scrollViewer.VerticalOffset <= 0;
        var scrollingDownAtBottom = e.Delta < 0 && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight;
        if (scrollingUpAtTop || scrollingDownAtBottom)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3d);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
