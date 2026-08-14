using System.Windows;
using BlueRelay.Diagnostics;
using BlueRelay.Persistence;
using BlueRelay.Presentation.ViewModels;
using BlueRelay.Services;
using BlueRelay.Services.Dialogs;

using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfApplication = System.Windows.Application;
using WindowInteropHelper = System.Windows.Interop.WindowInteropHelper;

namespace BlueRelay;

public partial class App : WpfApplication
{
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private TrayService? _trayService;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        StartupDiagnostics.Write("App constructed");
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        StartupDiagnostics.Write("Startup begin");
        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartupDiagnostics.Write("ShutdownMode set to OnExplicitShutdown");

            StartupDiagnostics.Write("Creating JsonStateStore");
            var stateStore = new JsonStateStore();
            StartupDiagnostics.Write($"Loading state from {stateStore.FilePath}");
            var loadResult = stateStore.LoadAsync().GetAwaiter().GetResult();
            StartupDiagnostics.Write("State load complete");
            var projectService = new ProjectService(
                loadResult.State,
                stateStore,
                new WorkflowStateMachine());

            var startupWarning = loadResult.Warning;
            if (!File.Exists(stateStore.FilePath) && !projectService.TrySave(out var initialSaveError))
            {
                startupWarning = string.IsNullOrWhiteSpace(startupWarning)
                    ? initialSaveError
                    : $"{startupWarning} {initialSaveError}";
            }
            StartupDiagnostics.Write("ProjectService initialized");

            StartupDiagnostics.Write("Constructing MainViewModel");
            _mainViewModel = new MainViewModel(
                loadResult.State,
                projectService,
                new MessageBoxDialogService(),
                new WindowsFolderPicker(),
                startupWarning);
            StartupDiagnostics.Write("MainViewModel initialized");

            StartupDiagnostics.Write("Constructing MainWindow and loading XAML");
            _mainWindow = new MainWindow
            {
                DataContext = _mainViewModel
            };
            StartupDiagnostics.Write("MainWindow initialized");

            StartupDiagnostics.Write("Initializing TrayService and NotifyIcon");
            _trayService = new TrayService(
                showRequested: ShowMainWindow,
                alwaysOnTopToggleRequested: ToggleAlwaysOnTop,
                exitRequested: ExitApplication);
            _trayService.SetAlwaysOnTop(_mainViewModel.IsAlwaysOnTop);
            StartupDiagnostics.Write("TrayService initialized");

            StartupDiagnostics.Write("Showing MainWindow");
            _mainWindow.Show();
            var nativeHandle = new WindowInteropHelper(_mainWindow).Handle;
            StartupDiagnostics.Write($"MainWindow.Show returned IsVisible={_mainWindow.IsVisible} Visibility={_mainWindow.Visibility} WindowState={_mainWindow.WindowState} NativeHandle={nativeHandle.ToInt64()}");
            StartupDiagnostics.Write("Startup complete");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.WriteException("Application_Startup", exception);
            WpfMessageBox.Show(
                $"BlueRelay could not start.\n\n{exception.Message}\n\nDetails were written to the BlueRelay startup log.",
                "BlueRelay startup error",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        StartupDiagnostics.Write($"Application exit code={e.ApplicationExitCode}");
        _trayService?.Dispose();
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.WriteException("DispatcherUnhandledException", e.Exception);
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            StartupDiagnostics.WriteException("AppDomain.UnhandledException", exception);
        }
        else
        {
            StartupDiagnostics.Write($"ERROR stage=AppDomain.UnhandledException object={e.ExceptionObject}");
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.ShowFromTray();
    }

    private void ToggleAlwaysOnTop()
    {
        _mainViewModel?.ToggleAlwaysOnTop();
        if (_mainViewModel is not null)
        {
            _trayService?.SetAlwaysOnTop(_mainViewModel.IsAlwaysOnTop);
        }
    }

    private void ExitApplication()
    {
        _trayService?.Dispose();
        _mainWindow?.CloseFromApplication();
        Shutdown();
    }
}
