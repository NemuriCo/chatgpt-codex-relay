using System.Windows;
using BlueRelay.Persistence;
using BlueRelay.Presentation.ViewModels;
using BlueRelay.Services;
using BlueRelay.Services.Dialogs;

using WpfApplication = System.Windows.Application;

namespace BlueRelay;

public partial class App : WpfApplication
{
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private TrayService? _trayService;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var stateStore = new JsonStateStore();
        var loadResult = stateStore.LoadAsync().GetAwaiter().GetResult();
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

        _mainViewModel = new MainViewModel(
            loadResult.State,
            projectService,
            new MessageBoxDialogService(),
            new WindowsFolderPicker(),
            startupWarning);

        _mainWindow = new MainWindow
        {
            DataContext = _mainViewModel
        };

        _trayService = new TrayService(
            showRequested: ShowMainWindow,
            alwaysOnTopToggleRequested: ToggleAlwaysOnTop,
            exitRequested: ExitApplication);
        _trayService.SetAlwaysOnTop(_mainViewModel.IsAlwaysOnTop);

        _mainWindow.Show();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _trayService?.Dispose();
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
