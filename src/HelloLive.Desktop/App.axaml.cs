using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using HelloLive.Core.Services.Monitoring;
using HelloLive.Core.Services.Settings;
using HelloLive.Core.Sites;
using HelloLive.Core.Sites.Douyin;
using HelloLive.Core.Sites.Kuaishou;
using HelloLive.Core.ViewModels;
using HelloLive.Core.Views;
using HelloLive.Desktop.Chromium;
using HelloLive.Desktop.FFmpeg;
using HelloLive.Desktop.Playwright;
using HelloLive.Desktop.Remote;
using HelloLive.Desktop.Recording;

namespace HelloLive.Desktop;

public partial class App : Application
{
    private static readonly TimeSpan MinimumSplashDisplayTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SplashCompletionHoldTime = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan TrayDoubleClickThreshold = TimeSpan.FromMilliseconds(600);

    private MainWindowViewModel? _viewModel;
    private PlaywrightLiveBrowserService? _browser;
    private RemoteApiHostService? _remoteApiHost;
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayShowMainWindowItem;
    private NativeMenuItem? _trayExitProgramItem;
    private DateTimeOffset? _lastTrayClickAt;
    private bool _desktopStartupStarted;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            desktop.Exit += Desktop_Exit;

            splash.Opened += async (_, _) =>
            {
                if (_desktopStartupStarted)
                    return;

                _desktopStartupStarted = true;
                await splash.WaitUntilPresentedAsync();
                var splashShownAt = DateTimeOffset.UtcNow;
                await InitializeDesktopAsync(desktop, splash, splashShownAt);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        DateTimeOffset splashShownAt)
    {
        try
        {
            splash.SetProgress(5, "正在启动 HelloLive…", "准备应用运行环境");
            await Task.Yield();

            splash.SetProgress(18, "正在加载设置…", "读取监控列表、主题与远程控制配置");
            var settingsService = new SettingsService();
            var monitorStore = new MonitorStore(settingsService.SettingsPath);

            splash.SetProgress(30, "正在加载平台模块…", "初始化快手、抖音直播适配器");
            var platforms = new LivePlatformRegistry(new ILivePlatformAdapter[]
            {
                new KuaishouLiveAdapter(),
                new DouyinLiveAdapter()
            });

            splash.SetProgress(44, "正在准备浏览器…", "初始化无头 Chromium 探测服务");
            var installer = new PlaywrightChromiumInstaller();
            _browser = new PlaywrightLiveBrowserService(installer);

            splash.SetProgress(55, "正在初始化录像服务…", "准备实时 FLV/HLS 录像与下载目录");
            var recorder = new LiveStreamRecorder();
            var coordinator = new LiveMonitorCoordinator(_browser, platforms, recorder);
            var ffmpegInstaller = new GyanFfmpegInstallerService();

            splash.SetProgress(66, "正在加载监控列表…", "恢复作者、头像缓存与监控策略");
            _viewModel = new MainWindowViewModel(
                _browser,
                ffmpegInstaller,
                platforms,
                coordinator,
                settingsService,
                monitorStore);

            _viewModel.RemoteApiEnabledChanged += ViewModel_RemoteApiEnabledChanged;
            _viewModel.RemoteApiPortChanged += ViewModel_RemoteApiPortChanged;

            await _viewModel.InitializeAsync();

            splash.SetProgress(82, "正在启动后台服务…", "应用远程控制服务器配置");
            _remoteApiHost = new RemoteApiHostService(_viewModel);
            await _remoteApiHost.SetEnabledAsync(_viewModel.RemoteApiEnabled);

            splash.SetProgress(94, "正在准备主界面…", "创建窗口、托盘图标与交互状态");
            var mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
            InitializeTrayIcon(mainWindow);

            splash.SetProgress(100, "启动完成", "HelloLive 已准备就绪");

            var elapsed = DateTimeOffset.UtcNow - splashShownAt;
            var remaining = MinimumSplashDisplayTime - elapsed;
            var hold = remaining > SplashCompletionHoldTime
                ? remaining
                : SplashCompletionHoldTime;
            await Task.Delay(hold);

            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            splash.Close();
        }
        catch (Exception ex)
        {
            splash.ShowFailure(ex.Message);
        }
    }

    private void InitializeTrayIcon(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;

        try
        {
            var showItem = _trayShowMainWindowItem = new NativeMenuItem
            {
                Header = "显示主界面"
            };
            var exitItem = _trayExitProgramItem = new NativeMenuItem
            {
                Header = "退出程序"
            };

            showItem.Click += TrayShowMainWindowItem_Click;
            exitItem.Click += TrayExitProgramItem_Click;

            var menu = new NativeMenu();
            menu.Add(showItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            // Desktop AssemblyName is explicitly "HelloLive".
            using var iconStream = AssetLoader.Open(
                new Uri("avares://HelloLive/Assets/app-icon.ico"));

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "HelloLive",
                Menu = menu,
                IsVisible = false
            };
            _trayIcon.Clicked += TrayIcon_Clicked;

            var trayIcons = new TrayIcons();
            trayIcons.Add(_trayIcon);
            TrayIcon.SetIcons(this, trayIcons);

            mainWindow.MinimizeToTrayRequested += MainWindow_MinimizeToTrayRequested;
        }
        catch
        {
            // Tray initialization failure must not stop the application.
            DisposeTrayIcon();
        }
    }

    private void MainWindow_MinimizeToTrayRequested(object? sender, EventArgs e)
    {
        if (_mainWindow is not { } mainWindow || _trayIcon is not { } trayIcon)
        {
            if (_mainWindow is { } fallbackWindow)
                fallbackWindow.WindowState = WindowState.Minimized;
            return;
        }

        _lastTrayClickAt = null;
        trayIcon.IsVisible = true;
        mainWindow.ShowInTaskbar = false;
        mainWindow.Hide();
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastTrayClickAt is { } last
            && now - last <= TrayDoubleClickThreshold)
        {
            _lastTrayClickAt = null;
            RestoreMainWindowFromTray();
            return;
        }

        _lastTrayClickAt = now;
    }

    private void TrayShowMainWindowItem_Click(object? sender, EventArgs e)
        => RestoreMainWindowFromTray();

    private void TrayExitProgramItem_Click(object? sender, EventArgs e)
    {
        RestoreMainWindowFromTray();
        _mainWindow?.RequestCloseConfirmation();
    }

    private void RestoreMainWindowFromTray()
    {
        if (_mainWindow is not { } mainWindow)
            return;

        mainWindow.ShowInTaskbar = true;
        if (!mainWindow.IsVisible)
            mainWindow.Show();

        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;

        mainWindow.Activate();

        if (_trayIcon is not null)
            _trayIcon.IsVisible = false;

        _lastTrayClickAt = null;
    }

    private void DisposeTrayIcon()
    {
        if (_mainWindow is not null)
            _mainWindow.MinimizeToTrayRequested -= MainWindow_MinimizeToTrayRequested;

        if (_trayShowMainWindowItem is not null)
            _trayShowMainWindowItem.Click -= TrayShowMainWindowItem_Click;

        if (_trayExitProgramItem is not null)
            _trayExitProgramItem.Click -= TrayExitProgramItem_Click;

        if (_trayIcon is not null)
        {
            _trayIcon.Clicked -= TrayIcon_Clicked;
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }

        TrayIcon.SetIcons(this, null);

        _trayIcon = null;
        _trayShowMainWindowItem = null;
        _trayExitProgramItem = null;
        _mainWindow = null;
        _lastTrayClickAt = null;
    }

    private async void ViewModel_RemoteApiEnabledChanged(object? sender, bool enabled)
    {
        if (_remoteApiHost is not null)
            await _remoteApiHost.SetEnabledAsync(enabled);
    }

    private async void ViewModel_RemoteApiPortChanged(object? sender, EventArgs e)
    {
        if (_remoteApiHost is not null && _viewModel?.RemoteApiEnabled == true)
            await _remoteApiHost.RestartAsync();
    }

    private async void Desktop_Exit(
        object? sender,
        ControlledApplicationLifetimeExitEventArgs e)
    {
        DisposeTrayIcon();

        if (_viewModel is not null)
        {
            _viewModel.RemoteApiEnabledChanged -= ViewModel_RemoteApiEnabledChanged;
            _viewModel.RemoteApiPortChanged -= ViewModel_RemoteApiPortChanged;
        }

        if (_remoteApiHost is not null)
        {
            await _remoteApiHost.DisposeAsync();
            _remoteApiHost = null;
        }

        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
            _viewModel = null;
        }

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }
    }
}
