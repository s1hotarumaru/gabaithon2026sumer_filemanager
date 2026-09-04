using System.ComponentModel;
using System.Windows;
using FileOrganizer.UI.Models;
using FileOrganizer.UI.Services;
using FileOrganizer.UI.ViewModels;
using FileOrganizer.Widgets.DropZone;
using FileOrganizer.Widgets.QuickLook;
using FileOrganizer.Widgets.Tray;

namespace FileOrganizer.UI;

public partial class App : Application
{
    private IFrontendBackendGateway? _backendGateway;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private TrayIconManager? _trayIcon;
    private DropShelfWindow? _dropShelfWindow;
    private QuickLookController? _quickLookController;
    private readonly CancellationTokenSource _applicationCancellation = new();
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _backendGateway = new ProductionBackendGateway();
        _backendGateway.ActivityOccurred += OnBackendActivity;

        _mainViewModel = new MainViewModel(_backendGateway);
        _mainWindow = new MainWindow(_mainViewModel);
        MainWindow = _mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Show();

        InitializeWidgets();

        await _mainViewModel.InitializeAsync(_applicationCancellation.Token);
        if (!_applicationCancellation.IsCancellationRequested)
        {
            _trayIcon?.SetMonitoringState(_mainViewModel.Dashboard.IsMonitoring);
            // InitializeWidgets()の時点ではSettings未ロードのため既定ショートカットで起動している。
            // 読み込み完了後、保存済みの値を反映する。
            _quickLookController?.UpdateShortcut(_mainViewModel.Settings.QuickLookShortcut);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _applicationCancellation.Cancel();
        if (_mainViewModel is not null)
            _mainViewModel.Dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        if (_dropShelfWindow is not null)
        {
            _dropShelfWindow.FilesSubmitted -= OnDroppedFilesSubmitted;
            _dropShelfWindow.Close();
        }
        _trayIcon?.Dispose();
        if (_mainViewModel is not null)
            _mainViewModel.Settings.QuickLookShortcutSaved -= OnQuickLookShortcutSaved;
        _quickLookController?.Dispose();
        if (_backendGateway is not null)
            _backendGateway.ActivityOccurred -= OnBackendActivity;
        if (_backendGateway is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _applicationCancellation.Dispose();
        base.OnExit(e);
    }

    private void InitializeWidgets()
    {
        if (_mainViewModel is null)
            return;

        try
        {
            _trayIcon = new TrayIconManager();
            _trayIcon.OpenRequested += (_, _) => ShowMainWindow();
            _trayIcon.DryRunRequested += (_, _) =>
            {
                ShowMainWindow();
                _mainViewModel.Dashboard.RequestDryRunCommand.Execute(null);
            };
            _trayIcon.DropZoneRequested += (_, _) => ShowDropZone();
            _trayIcon.MonitoringToggleRequested += (_, args) =>
            {
                if (_mainViewModel.Dashboard.IsMonitoring != args.Enabled)
                    _mainViewModel.Dashboard.ToggleMonitoringCommand.Execute(null);
            };
            _trayIcon.ExitRequested += (_, _) => ExitApplication();
            _mainViewModel.Dashboard.DropZoneRequested += (_, _) => ShowDropZone();
            _mainViewModel.Dashboard.PropertyChanged += OnDashboardPropertyChanged;
        }
        catch (Exception ex)
        {
            // Explorer/通知領域が利用できない環境でもメインUIは継続する。
            _mainViewModel.ShowMessage($"タスクトレイを初期化できませんでした: {ex.Message}");
        }

        try
        {
            _quickLookController = new QuickLookController(
                () => _mainViewModel?.Settings.IsQuickLookEnabled == true,
                Dispatcher,
                _mainViewModel.Settings.QuickLookShortcut);
            _mainViewModel.Settings.QuickLookShortcutSaved += OnQuickLookShortcutSaved;
        }
        catch (Exception ex)
        {
            _mainViewModel.ShowMessage($"Quick Lookを初期化できませんでした: {ex.Message}");
        }
    }

    private void OnQuickLookShortcutSaved(string normalizedShortcut) => _quickLookController?.UpdateShortcut(normalizedShortcut);

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowDropZone()
    {
        if (_dropShelfWindow is null)
        {
            _dropShelfWindow = new DropShelfWindow();
            _dropShelfWindow.FilesSubmitted += OnDroppedFilesSubmitted;
            _dropShelfWindow.Closed += OnDropShelfClosed;
        }

        _dropShelfWindow.Show();
        _dropShelfWindow.Activate();
    }

    private void OnDroppedFilesSubmitted(object? sender, DroppedFilesSubmittedEventArgs e)
    {
        ShowMainWindow();
        _mainWindow?.ShowDryRunForFiles(e.Paths);
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsMonitoring) && _mainViewModel is not null)
            _trayIcon?.SetMonitoringState(_mainViewModel.Dashboard.IsMonitoring);
    }

    private void OnDropShelfClosed(object? sender, EventArgs e)
    {
        if (_dropShelfWindow is null)
            return;
        _dropShelfWindow.FilesSubmitted -= OnDroppedFilesSubmitted;
        _dropShelfWindow.Closed -= OnDropShelfClosed;
        _dropShelfWindow = null;
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
            return;

        if (_trayIcon is null)
        {
            // トレイ初期化に失敗した環境では、画面を閉じられなくなる事態を避けて通常終了する。
            _isExiting = true;
            Dispatcher.BeginInvoke(new Action(Shutdown));
            return;
        }

        // 常駐仕様: タイトルバーの×は終了ではなくトレイへ格納する。
        e.Cancel = true;
        _mainWindow?.Hide();
        _trayIcon?.ShowNotification("File Organizer", "フォルダの監視を続けています。終了はトレイメニューから行えます。");
    }

    private async void ExitApplication()
    {
        _isExiting = true;
        _applicationCancellation.Cancel();
        if (_backendGateway is not null)
        {
            try { await _backendGateway.ShutdownAsync(); }
            catch { /* 終了処理は継続する。Job Object破棄はOnExitで必ず行う。 */ }
        }
        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Close();
        }
        Shutdown();
    }

    private void OnBackendActivity(object? sender, BackendActivityEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Message) || _mainViewModel?.Settings.EnableToastNotifications != true)
            return;
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { _trayIcon?.ShowNotification("File Organizer", e.Message); }
            catch (ObjectDisposedException) { }
        }));
    }
}
