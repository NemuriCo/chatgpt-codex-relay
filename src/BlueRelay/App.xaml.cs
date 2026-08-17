using System.Windows;
using BlueRelay.Diagnostics;
using BlueRelay.Localization;
using BlueRelay.Persistence;
using BlueRelay.Presentation.ViewModels;
using BlueRelay.Services;
using BlueRelay.Services.Bridges;
using BlueRelay.Services.Codex;
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
    private BrowserBridgeServer? _browserBridgeServer;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        StartupDiagnostics.Write("App constructed");
    }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        StartupDiagnostics.Write("Startup begin");
        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartupDiagnostics.Write("ShutdownMode set to OnExplicitShutdown");

            StartupDiagnostics.Write("Creating JsonStateStore");
            var stateStore = new JsonStateStore();
            StartupDiagnostics.Write($"Loading state from {stateStore.FilePath}");
            var loadResult = await stateStore.LoadAsync();
            StartupDiagnostics.Write("State load complete");

            StartupDiagnostics.Write("Creating ProjectService");
            var projectService = new ProjectService(
                loadResult.State,
                stateStore,
                new WorkflowStateMachine());

            var startupWarning = loadResult.Warning;
            if (!File.Exists(stateStore.FilePath) || loadResult.WasMigrated)
            {
                StartupDiagnostics.Write(loadResult.WasMigrated ? "Migrated state save begin" : "Initial state save begin");
                var initialSaveResult = await projectService.TrySaveAsync();
                StartupDiagnostics.Write(loadResult.WasMigrated ? "Migrated state save complete" : "Initial state save complete");
                if (!initialSaveResult.Success)
                {
                    startupWarning = string.IsNullOrWhiteSpace(startupWarning)
                        ? initialSaveResult.Error
                        : $"{startupWarning} {initialSaveResult.Error}";
                }
            }
            StartupDiagnostics.Write("ProjectService initialized");

            StartupDiagnostics.Write("Creating BrowserBridgeService");
            var codexBridge = new CodexAppServerBridge(loadResult.State);
            var browserBridge = new BrowserBridgeService(loadResult.State, projectService, codexBridge);
            _browserBridgeServer = new BrowserBridgeServer(browserBridge);
            StartupDiagnostics.Write("BrowserBridgeService initialized");

            StartupDiagnostics.Write("Constructing MainViewModel");
            _mainViewModel = new MainViewModel(
                loadResult.State,
                projectService,
                new MessageBoxDialogService(),
                new WindowsFolderPicker(LocalizationService.Current),
                new GitRepositoryDetector(),
                startupWarning,
                browserBridge,
                codexBridge);
            StartupDiagnostics.Write("MainViewModel initialized");

            var bridgeStartResult = await _browserBridgeServer.StartAsync();
            _mainViewModel.SetBrowserBridgeStatus(bridgeStartResult.Success, bridgeStartResult.Error);
            StartupDiagnostics.Write(bridgeStartResult.Success
                ? $"BrowserBridge ready port={_browserBridgeServer.Port}"
                : "BrowserBridge unavailable; BlueRelay will continue without browser connectivity");

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
                exitRequested: ExitApplication,
                text: _mainViewModel.Ui);
            _trayService.SetAlwaysOnTop(_mainViewModel.IsAlwaysOnTop);
            StartupDiagnostics.Write("TrayService initialized");

            StartupDiagnostics.Write("MainWindow.Show");
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

    private async void Application_Exit(object sender, ExitEventArgs e)
    {
        StartupDiagnostics.Write($"Application exit code={e.ApplicationExitCode}");
        _trayService?.Dispose();
        if (_browserBridgeServer is not null)
        {
            await _browserBridgeServer.StopAsync();
        }

        if (_mainViewModel is not null)
        {
            await _mainViewModel.StopCodexAsync();
        }
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

    private async void ToggleAlwaysOnTop()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        await _mainViewModel.ToggleAlwaysOnTopAsync();
        _trayService?.SetAlwaysOnTop(_mainViewModel.IsAlwaysOnTop);
    }

    private async void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            await _mainWindow.SaveWindowSettingsAsync();
        }

        _trayService?.Dispose();
        if (_browserBridgeServer is not null)
        {
            await _browserBridgeServer.StopAsync();
        }
        if (_mainViewModel is not null)
        {
            await _mainViewModel.StopCodexAsync();
        }
        _mainWindow?.CloseFromApplication();
        Shutdown();
    }
}
