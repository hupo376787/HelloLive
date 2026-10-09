using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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
    private static readonly TimeSpan SplashCompletionHoldTime = TimeSpan.FromMilliseconds(120);
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
            splash.SetProgress(
                0,
                "正在启动 HelloLive…",
                "初始化应用运行环境");
            await Task.Yield();

            splash.SetProgress(
                0,
                "正在读取设置…",
                "读取应用设置和监控数据路径");
            var settingsService = new SettingsService();
            var monitorStore = new MonitorStore(settingsService.SettingsPath);
            splash.SetProgress(
                8,
                "设置读取完成",
                "应用配置与监控存储已准备");

            splash.SetProgress(
                8,
                "正在加载平台解析器…",
                "创建已启用的直播平台适配器");
            var platforms = new LivePlatformRegistry(new ILivePlatformAdapter[]
            {
                new KuaishouLiveAdapter(),
                new DouyinLiveAdapter()
            });
            splash.SetProgress(
                16,
                "平台解析器已就绪",
                $"已加载 {platforms.Platforms.Count} 个直播平台适配器");

            splash.SetProgress(
                16,
                "正在准备浏览器服务…",
                "初始化 Chrome for Testing / Playwright 探测服务");
            var installer = new PlaywrightChromiumInstaller();
            _browser = new PlaywrightLiveBrowserService(installer);
            splash.SetProgress(
                24,
                "浏览器服务已创建",
                "直播页面探测服务已准备");

            splash.SetProgress(
                24,
                "正在准备录像服务…",
                "初始化 FLV/HLS 录像、调度器和 FFmpeg 服务");
            var recorder = new LiveStreamRecorder();
            var coordinator = new LiveMonitorCoordinator(
                _browser,
                platforms,
                recorder);
            var ffmpegInstaller = new GyanFfmpegInstallerService();
            splash.SetProgress(
                32,
                "录像服务已创建",
                "录像器与直播监控调度器已准备");

            splash.SetProgress(
                32,
                "正在扫描历史录像…",
                "检查 Download 目录中的 FLV/MP4 时长与实际时间戳");

            var toolInfo = ffmpegInstaller.GetToolInfo();
            var videoRepair = new StartupVideoRepairService();
            var repairSummary = await Task.Run(
                () => videoRepair.ScanAndRepairAsync(
                    recorder.DownloadRoot,
                    toolInfo.FfmpegPath,
                    toolInfo.FfprobePath,
                    progress =>
                    {
                        var fraction = progress.TotalCount > 0
                            ? Math.Clamp(
                                progress.ProcessedCount / (double)progress.TotalCount,
                                0d,
                                1d)
                            : 1d;

                        Dispatcher.UIThread.Post(() =>
                            splash.SetProgress(
                                32d + 20d * fraction,
                                "正在检查历史录像…",
                                progress.Message));
                    }));

            var repairDetail = repairSummary.SkippedBecauseFfmpegMissing
                ? $"发现 {repairSummary.TotalCount} 个录像文件；未找到 EXE 目录中的 FFmpeg/FFprobe，已跳过修复"
                : $"已检查 {repairSummary.TotalCount} 个录像，修复 {repairSummary.RepairedCount} 个，正常 {repairSummary.HealthyCount} 个，失败 {repairSummary.FailedCount} 个";

            splash.SetProgress(
                52,
                "历史录像检查完成",
                repairDetail);

            splash.SetProgress(
                52,
                "正在读取监控列表…",
                "创建主视图模型并恢复已保存的监控对象");
            _viewModel = new MainWindowViewModel(
                _browser,
                ffmpegInstaller,
                platforms,
                coordinator,
                settingsService,
                monitorStore);

            _viewModel.RemoteApiEnabledChanged += ViewModel_RemoteApiEnabledChanged;
            _viewModel.RemoteApiPortChanged += ViewModel_RemoteApiPortChanged;

            splash.SetProgress(
                58,
                "监控列表已读取",
                $"已恢复 {_viewModel.MonitorCount} 个监控对象");

            const double initializeStart = 58d;
            const double initializeRange = 24d;
            await _viewModel.InitializeAsync(
                (fraction, status, detail) =>
                {
                    var bounded = Math.Clamp(fraction, 0d, 1d);
                    splash.SetProgress(
                        initializeStart + initializeRange * bounded,
                        status,
                        detail);
                });

            splash.SetProgress(
                82,
                "正在应用后台服务配置…",
                "根据设置启动或保持关闭远程控制服务器");
            _remoteApiHost = new RemoteApiHostService(_viewModel);
            await _remoteApiHost.SetEnabledAsync(_viewModel.RemoteApiEnabled);
            splash.SetProgress(
                90,
                "后台服务已就绪",
                _viewModel.RemoteApiEnabled
                    ? "远程控制服务器已按当前配置启动"
                    : "远程控制服务器当前未启用");

            splash.SetProgress(
                90,
                "正在创建主界面…",
                "创建主窗口并绑定监控状态");
            var mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
            splash.SetProgress(
                96,
                "主界面已创建",
                "正在初始化系统托盘");

            InitializeTrayIcon(mainWindow);

            splash.SetProgress(
                100,
                "启动完成",
                "所有启动步骤均已完成，正在打开主界面");

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
