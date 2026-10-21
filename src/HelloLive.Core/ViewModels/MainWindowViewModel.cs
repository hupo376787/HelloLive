using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelloLive.Core.Contracts;
using HelloLive.Core.Models;
using HelloLive.Core.Services.Browser;
using HelloLive.Core.Services.FFmpeg;
using HelloLive.Core.Services.Images;
using HelloLive.Core.Services.Monitoring;
using HelloLive.Core.Services.Notifications;
using HelloLive.Core.Services.Recording;
using HelloLive.Core.Services.Settings;
using HelloLive.Core.Sites;
using HelloLive.Core.Utilities;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace HelloLive.Core.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILiveBrowserService _browser;
    private readonly IFfmpegInstallerService _ffmpegInstaller;
    private readonly LivePlatformRegistry _platforms;
    private readonly LiveMonitorCoordinator _coordinator;
    private readonly SettingsService _settingsService;
    private readonly MonitorStore _monitorStore;
    private readonly AppSettings _settings;
    private readonly ImageCacheService _imageCache = new();
    private readonly PushPlusNotificationService _pushPlusNotification = new();
    private readonly Dictionary<string, string> _loadedAvatarUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _lastConfirmedLiveStates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _folderSizeGate = new(2, 2);
    private readonly ConcurrentDictionary<string, int> _folderSizeRefreshVersions =
        new(StringComparer.Ordinal);

    private string _newMonitorUrl = string.Empty;
    private string _newMonitorName = string.Empty;
    private bool _headlessMode;
    private int _maxConcurrentPages;
    private int _checkIntervalSeconds;
    private int _checkTimeoutSeconds;
    private bool _blockImagesAndFonts;
    private bool _autoStartMonitoring;
    private bool _isMonitoring;
    private bool _isMonitorPanelVisible;
    private bool _isMonitorGridExpanded;
    private string _monitorSearchText = string.Empty;
    private bool _showLiveMonitorsOnly;
    private double _monitorGridItemWidth = 340d;
    private double _monitorGridAvailableWidth;
    private int _monitorGridSizeMode = 1;
    private string _pushPlusToken = string.Empty;
    private string _currentTask = "等待任务";
    private LiveMonitorTarget? _currentStatusTarget;
    private string _browserStatusText = "尚未检查 Chromium";
    private bool _isChromiumInstalling;
    private bool _isChromiumInstallProgressVisible;
    private bool _isChromiumInstallProgressIndeterminate;
    private double _chromiumInstallProgressPercent;
    private string _chromiumInstallProgressText = string.Empty;
    private bool _isFfmpegInstalling;
    private bool _isFfmpegInstallProgressVisible;
    private bool _isFfmpegInstallProgressIndeterminate;
    private double _ffmpegInstallProgressPercent;
    private string _ffmpegInstallProgressText = string.Empty;
    private string _ffmpegStatusText = "尚未检查 FFmpeg";
    private bool _compressAfterRecording;
    private bool _deleteOriginalAfterCompression;
    private string _themeIcon = "☾";
    private bool _remoteApiEnabled;
    private int _remoteApiPort;
    private string _remoteApiToken = string.Empty;
    private string _remoteApiStatusText = "远程控制服务器未启动";
    private LowDiskSpaceInfo? _pendingLowDiskSpaceAlert;
    private int _shutdownPrepared;

    public MainWindowViewModel(
        ILiveBrowserService browser,
        IFfmpegInstallerService ffmpegInstaller,
        LivePlatformRegistry platforms,
        LiveMonitorCoordinator coordinator,
        SettingsService settingsService,
        MonitorStore monitorStore)
    {
        _browser = browser;
        _ffmpegInstaller = ffmpegInstaller;
        _platforms = platforms;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _monitorStore = monitorStore;
        _settings = settingsService.Load();

        // This switch controls user-triggered checks only. Background polling is
        // forced to headless inside LiveMonitorCoordinator and never uses this value.
        _headlessMode = _settings.HeadlessMode;
        _maxConcurrentPages = _settings.MaxConcurrentPages;
        _checkIntervalSeconds = _settings.CheckIntervalSeconds;
        _checkTimeoutSeconds = _settings.CheckTimeoutSeconds;
        _blockImagesAndFonts = _settings.BlockImagesAndFonts;
        _autoStartMonitoring = _settings.AutoStartMonitoring;
        _isMonitorPanelVisible = _settings.MonitorPanelVisible;
        _compressAfterRecording = _settings.CompressAfterRecording;
        _deleteOriginalAfterCompression = _settings.DeleteOriginalAfterCompression;
        _pushPlusToken = _settings.PushPlusToken ?? string.Empty;
        _remoteApiEnabled = _settings.RemoteApiEnabled;
        _remoteApiPort = _settings.RemoteApiPort;
        _remoteApiToken = _settings.RemoteApiToken;

        AddMonitorCommand = new AsyncRelayCommand(AddMonitorAsync);
        OpenBrowserCommand = new AsyncRelayCommand(() => OpenBrowserAsync());
        StartMonitoringCommand = new AsyncRelayCommand(StartMonitoringAsync);
        StopMonitoringCommand = new AsyncRelayCommand(StopMonitoringAsync);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync);
        InstallChromiumCommand = new AsyncRelayCommand(InstallChromiumAsync, () => !IsChromiumInstalling);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync, () => !IsFfmpegInstalling && IsFfmpegAutoInstallSupported);
        CheckTargetCommand = new AsyncRelayCommand<LiveMonitorTarget>(CheckTargetAsync);
        RemoveMonitorCommand = new AsyncRelayCommand<LiveMonitorTarget>(RemoveMonitorAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleMonitorPanelCommand = new RelayCommand(ToggleMonitorPanel);
        ToggleMonitorGridCommand = new RelayCommand(ToggleMonitorGrid);
        ClearMonitorSearchCommand = new RelayCommand(
            () => MonitorSearchText = string.Empty);

        foreach (var target in _monitorStore.Load())
            AttachTarget(target);

        _coordinator.CheckResultChanged += Coordinator_CheckResultChanged;
        _coordinator.RecordingStateChanged += Coordinator_RecordingStateChanged;
        _coordinator.StorageChanged += Coordinator_StorageChanged;
        _coordinator.LowDiskSpaceDetected += Coordinator_LowDiskSpaceDetected;
        _coordinator.Log += Coordinator_Log;
        _coordinator.RunningChanged += Coordinator_RunningChanged;

        ApplyTheme(_settings.Theme);
        RefreshCoordinatorState();
        RaiseMetricsChanged();
    }

    public event EventHandler<bool>? RemoteApiEnabledChanged;
    public event EventHandler? RemoteApiPortChanged;
    public event Action<LowDiskSpaceInfo>? LowDiskSpaceAlertRequested;

    public ObservableCollection<LiveMonitorTarget> Monitors { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public IReadOnlyList<PlatformOption> Platforms => _platforms.Platforms;

    public IAsyncRelayCommand AddMonitorCommand { get; }
    public IAsyncRelayCommand OpenBrowserCommand { get; }
    public IAsyncRelayCommand StartMonitoringCommand { get; }
    public IAsyncRelayCommand StopMonitoringCommand { get; }
    public IAsyncRelayCommand CheckAllCommand { get; }
    public IAsyncRelayCommand InstallChromiumCommand { get; }
    public IAsyncRelayCommand InstallFfmpegCommand { get; }
    public IAsyncRelayCommand<LiveMonitorTarget> CheckTargetCommand { get; }
    public IAsyncRelayCommand<LiveMonitorTarget> RemoveMonitorCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IRelayCommand ToggleMonitorPanelCommand { get; }
    public IRelayCommand ToggleMonitorGridCommand { get; }
    public IRelayCommand ClearMonitorSearchCommand { get; }

    public string NewMonitorUrl
    {
        get => _newMonitorUrl;
        set => SetProperty(ref _newMonitorUrl, value ?? string.Empty);
    }

    public string NewMonitorName
    {
        get => _newMonitorName;
        set => SetProperty(ref _newMonitorName, value ?? string.Empty);
    }

    public bool HeadlessMode
    {
        get => _headlessMode;
        set
        {
            if (!SetProperty(ref _headlessMode, value))
                return;
            _settings.HeadlessMode = value;
            PersistSettingsSoon();
            RefreshCoordinatorState();
        }
    }

    public string PushPlusToken
    {
        get => _pushPlusToken;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (!SetProperty(ref _pushPlusToken, normalized))
                return;

            _settings.PushPlusToken = normalized;
            PersistSettingsSoon();
        }
    }

    public int MaxConcurrentPages
    {
        get => _maxConcurrentPages;
        set
        {
            var normalized = Math.Clamp(value, 1, 8);
            if (!SetProperty(ref _maxConcurrentPages, normalized))
                return;
            _settings.MaxConcurrentPages = normalized;
            PersistSettingsSoon();
            RefreshCoordinatorState();
        }
    }

    public int CheckIntervalSeconds
    {
        get => _checkIntervalSeconds;
        set
        {
            var normalized = Math.Clamp(value, 10, 3600);
            if (!SetProperty(ref _checkIntervalSeconds, normalized))
                return;
            _settings.CheckIntervalSeconds = normalized;
            PersistSettingsSoon();
            RefreshCoordinatorState();
        }
    }

    public int CheckTimeoutSeconds
    {
        get => _checkTimeoutSeconds;
        set
        {
            var normalized = Math.Clamp(value, 5, 120);
            if (!SetProperty(ref _checkTimeoutSeconds, normalized))
                return;
            _settings.CheckTimeoutSeconds = normalized;
            PersistSettingsSoon();
            RefreshCoordinatorState();
        }
    }

    public bool BlockImagesAndFonts
    {
        get => _blockImagesAndFonts;
        set
        {
            if (!SetProperty(ref _blockImagesAndFonts, value))
                return;
            _settings.BlockImagesAndFonts = value;
            PersistSettingsSoon();
            RefreshCoordinatorState();
        }
    }

    public bool AutoStartMonitoring
    {
        get => _autoStartMonitoring;
        set
        {
            if (!SetProperty(ref _autoStartMonitoring, value))
                return;
            _settings.AutoStartMonitoring = value;
            PersistSettingsSoon();
        }
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
                OnPropertyChanged(nameof(MonitoringStatusText));
        }
    }

    public bool IsMonitorPanelVisible
    {
        get => _isMonitorPanelVisible;
        set
        {
            if (!SetProperty(ref _isMonitorPanelVisible, value))
                return;

            if (!value && IsMonitorGridExpanded)
                IsMonitorGridExpanded = false;

            OnPropertyChanged(nameof(IsCompactMonitorPanelVisible));
            OnPropertyChanged(nameof(MonitorPanelButtonText));
            _settings.MonitorPanelVisible = value;
            PersistSettingsSoon();
        }
    }

    public bool IsMonitorGridExpanded
    {
        get => _isMonitorGridExpanded;
        private set
        {
            if (!SetProperty(ref _isMonitorGridExpanded, value))
                return;

            OnPropertyChanged(nameof(IsCompactMonitorPanelVisible));
            OnPropertyChanged(nameof(IsMainWorkspaceVisible));
            OnPropertyChanged(nameof(MonitorGridToggleIcon));
            OnPropertyChanged(nameof(MonitorGridToggleToolTip));
        }
    }

    public bool IsCompactMonitorPanelVisible
        => IsMonitorPanelVisible && !IsMonitorGridExpanded;

    public bool IsMainWorkspaceVisible
        => !IsMonitorGridExpanded;

    public string MonitorSearchText
    {
        get => _monitorSearchText;
        set
        {
            if (!SetProperty(ref _monitorSearchText, value ?? string.Empty))
                return;

            OnPropertyChanged(nameof(HasMonitorSearchText));
            OnPropertyChanged(nameof(FilteredMonitors));
        }
    }

    public bool HasMonitorSearchText
        => !string.IsNullOrWhiteSpace(MonitorSearchText);

    public bool ShowLiveMonitorsOnly
    {
        get => _showLiveMonitorsOnly;
        set
        {
            if (!SetProperty(ref _showLiveMonitorsOnly, value))
                return;

            OnPropertyChanged(nameof(FilteredMonitors));
        }
    }

    public IEnumerable<LiveMonitorTarget> FilteredMonitors
    {
        get
        {
            IEnumerable<LiveMonitorTarget> items = Monitors;

            if (ShowLiveMonitorsOnly)
                items = items.Where(target => target.IsLive);

            var keyword = MonitorSearchText.Trim();
            if (keyword.Length == 0)
                return items;

            return items.Where(target =>
                ContainsSearchText(target.DisplayName, keyword)
                || ContainsSearchText(target.AuthorId, keyword)
                || ContainsSearchText(target.ProfileUrl, keyword)
                || ContainsSearchText(target.PlatformText, keyword)
                || ContainsSearchText(target.StatusMessage, keyword));
        }
    }

    private static bool ContainsSearchText(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(
               keyword,
               StringComparison.CurrentCultureIgnoreCase);

    public double MonitorGridItemWidth
    {
        get => _monitorGridItemWidth;
        private set => SetProperty(ref _monitorGridItemWidth, value);
    }

    public bool IsMonitorGridSmall
    {
        get => _monitorGridSizeMode == 0;
        set
        {
            if (value)
                SetMonitorGridSizeMode(0);
            else if (_monitorGridSizeMode == 0)
                OnPropertyChanged(nameof(IsMonitorGridSmall));
        }
    }

    public bool IsMonitorGridMedium
    {
        get => _monitorGridSizeMode == 1;
        set
        {
            if (value)
                SetMonitorGridSizeMode(1);
            else if (_monitorGridSizeMode == 1)
                OnPropertyChanged(nameof(IsMonitorGridMedium));
        }
    }

    public bool IsMonitorGridLarge
    {
        get => _monitorGridSizeMode == 2;
        set
        {
            if (value)
                SetMonitorGridSizeMode(2);
            else if (_monitorGridSizeMode == 2)
                OnPropertyChanged(nameof(IsMonitorGridLarge));
        }
    }

    private void SetMonitorGridSizeMode(int mode)
    {
        mode = Math.Clamp(mode, 0, 2);
        if (_monitorGridSizeMode == mode)
            return;

        _monitorGridSizeMode = mode;
        OnPropertyChanged(nameof(IsMonitorGridSmall));
        OnPropertyChanged(nameof(IsMonitorGridMedium));
        OnPropertyChanged(nameof(IsMonitorGridLarge));

        if (_monitorGridAvailableWidth > 0)
            RecalculateMonitorGridItemWidth();
    }

    public void UpdateMonitorGridAvailableWidth(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
            return;

        _monitorGridAvailableWidth = availableWidth;
        RecalculateMonitorGridItemWidth();
    }

    private void RecalculateMonitorGridItemWidth()
    {
        var usableWidth = Math.Max(300d, _monitorGridAvailableWidth - 14d);

        // Medium is the existing layout. Small doubles the medium column count,
        // so each card is approximately half the current width. Large keeps the
        // current width; its height is doubled by the XAML large-size style.
        var mediumColumns = Math.Max(
            1,
            (int)Math.Floor(usableWidth / 315d));
        mediumColumns = Math.Min(mediumColumns, 6);

        var columns = _monitorGridSizeMode == 0
            ? Math.Min(mediumColumns * 2, 12)
            : mediumColumns;

        MonitorGridItemWidth =
            Math.Floor((usableWidth / columns) * 10d) / 10d;
    }

    public string MonitorGridToggleIcon
        => IsMonitorGridExpanded ? "⤡" : "⤢";

    public string MonitorGridToggleToolTip
        => IsMonitorGridExpanded ? "恢复监控列表侧栏" : "展开监控列表";

    public string CurrentTask
    {
        get => _currentTask;
        private set => SetProperty(ref _currentTask, value);
    }

    public LiveMonitorTarget? CurrentStatusTarget
    {
        get => _currentStatusTarget;
        private set => SetProperty(ref _currentStatusTarget, value);
    }

    public string BrowserStatusText
    {
        get => _browserStatusText;
        private set => SetProperty(ref _browserStatusText, value);
    }

    public bool IsChromiumInstalling
    {
        get => _isChromiumInstalling;
        private set
        {
            if (!SetProperty(ref _isChromiumInstalling, value))
                return;
            InstallChromiumCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsChromiumInstallProgressVisible
    {
        get => _isChromiumInstallProgressVisible;
        private set => SetProperty(ref _isChromiumInstallProgressVisible, value);
    }

    public bool IsChromiumInstallProgressIndeterminate
    {
        get => _isChromiumInstallProgressIndeterminate;
        private set => SetProperty(ref _isChromiumInstallProgressIndeterminate, value);
    }

    public double ChromiumInstallProgressPercent
    {
        get => _chromiumInstallProgressPercent;
        private set => SetProperty(ref _chromiumInstallProgressPercent, value);
    }

    public string ChromiumInstallProgressText
    {
        get => _chromiumInstallProgressText;
        private set => SetProperty(ref _chromiumInstallProgressText, value);
    }

    public bool IsFfmpegInstalling
    {
        get => _isFfmpegInstalling;
        private set
        {
            if (!SetProperty(ref _isFfmpegInstalling, value))
                return;
            OnPropertyChanged(nameof(InstallFfmpegButtonText));
            InstallFfmpegCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsFfmpegInstallProgressVisible
    {
        get => _isFfmpegInstallProgressVisible;
        private set => SetProperty(ref _isFfmpegInstallProgressVisible, value);
    }

    public bool IsFfmpegInstallProgressIndeterminate
    {
        get => _isFfmpegInstallProgressIndeterminate;
        private set => SetProperty(ref _isFfmpegInstallProgressIndeterminate, value);
    }

    public double FfmpegInstallProgressPercent
    {
        get => _ffmpegInstallProgressPercent;
        private set => SetProperty(ref _ffmpegInstallProgressPercent, value);
    }

    public string FfmpegInstallProgressText
    {
        get => _ffmpegInstallProgressText;
        private set => SetProperty(ref _ffmpegInstallProgressText, value);
    }

    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => SetProperty(ref _ffmpegStatusText, value);
    }

    public bool IsFfmpegAutoInstallSupported => _ffmpegInstaller.IsSupported;

    public bool CompressAfterRecording
    {
        get => _compressAfterRecording;
        set
        {
            if (!SetProperty(ref _compressAfterRecording, value))
                return;

            _settings.CompressAfterRecording = value;
            PersistSettingsSoon();
            OnPropertyChanged(nameof(CanDeleteOriginalAfterCompression));
            RefreshCoordinatorState();
        }
    }

    public bool DeleteOriginalAfterCompression
    {
        get => _deleteOriginalAfterCompression;
        set
        {
            if (!SetProperty(ref _deleteOriginalAfterCompression, value))
                return;

            _settings.DeleteOriginalAfterCompression = value;
            PersistSettingsSoon();
            RefreshCoordinatorState();
        }
    }

    public bool CanDeleteOriginalAfterCompression => CompressAfterRecording;

    public string InstallFfmpegButtonText => IsFfmpegInstalling
        ? "正在下载 FFmpeg…"
        : _ffmpegInstaller.IsInstalled
            ? "重新下载 FFmpeg"
            : "下载 FFmpeg";

    public string ThemeIcon
    {
        get => _themeIcon;
        private set => SetProperty(ref _themeIcon, value);
    }

    public bool RemoteApiEnabled
    {
        get => _remoteApiEnabled;
        set
        {
            if (!SetProperty(ref _remoteApiEnabled, value))
                return;

            _settings.RemoteApiEnabled = value;
            PersistSettingsSoon();
            RemoteApiEnabledChanged?.Invoke(this, value);
        }
    }

    public int RemoteApiPort
    {
        get => _remoteApiPort;
        set
        {
            var normalized = Math.Clamp(value, 1024, 65535);
            if (!SetProperty(ref _remoteApiPort, normalized))
                return;

            _settings.RemoteApiPort = normalized;
            PersistSettingsSoon();
            RemoteApiPortChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string RemoteApiToken
    {
        get => _remoteApiToken;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (!SetProperty(ref _remoteApiToken, normalized))
                return;

            _settings.RemoteApiToken = normalized;
            PersistSettingsSoon();
        }
    }

    public string RemoteApiStatusText
    {
        get => _remoteApiStatusText;
        private set => SetProperty(ref _remoteApiStatusText, value);
    }

    public int MonitorCount => Monitors.Count;
    public int EnabledCount => Monitors.Count(x => x.IsEnabled);
    public int LiveCount => Monitors.Count(x => x.State == LiveMonitorState.Live);
    public int NotDetectedCount => Monitors.Count(x => x.State == LiveMonitorState.NotDetected);
    public int CheckingCount => Monitors.Count(x => x.State == LiveMonitorState.Checking);
    public int FailedCount => Monitors.Count(x => x.State == LiveMonitorState.Error);
    public string MonitoringStatusText => IsMonitoring ? "监控运行中" : "监控已停止";
    public string MonitorPanelButtonText => IsMonitorPanelVisible ? "隐藏监控列表" : "显示监控列表";
    public string DownloadRoot => _coordinator.DownloadRoot;

    public async Task InitializeAsync(
        Action<double, string, string>? startupProgress = null)
    {
        startupProgress?.Invoke(
            0d,
            "正在检查浏览器…",
            "检测 EXE 目录中的 Chrome for Testing");

        var path = await _browser.FindInstalledChromiumPathAsync();
        BrowserStatusText = string.IsNullOrWhiteSpace(path)
            ? $"未发现 Chromium，可点击安装。安装目录：{_browser.PreferredChromiumInstallDirectory}"
            : $"Chromium：{path}";

        startupProgress?.Invoke(
            0.40d,
            "正在检查 FFmpeg…",
            "检测 HLS 录像所需的 FFmpeg 可执行文件");

        RefreshFfmpegStatus();

        startupProgress?.Invoke(
            0.70d,
            "正在恢复监控状态…",
            $"已读取 {Monitors.Count} 个监控对象，正在应用启动策略");

        AddLog($"已加载 {Monitors.Count} 个监控对象。后台轮询与单项检查固定使用无头 Chromium，当前最大并发页面数：{MaxConcurrentPages}。");

        if (AutoStartMonitoring && Monitors.Any(x => x.IsEnabled))
        {
            startupProgress?.Invoke(
                0.85d,
                "正在启动监控…",
                $"自动启动已启用，准备监控 {Monitors.Count(x => x.IsEnabled)} 个对象");
            await StartMonitoringAsync();
        }

        startupProgress?.Invoke(
            1d,
            "监控状态已就绪",
            $"已恢复 {Monitors.Count} 个监控对象");
    }

    public async Task OpenBrowserAsync(string? input = null)
    {
        var source = string.IsNullOrWhiteSpace(input)
            ? NewMonitorUrl.Trim()
            : input.Trim();
        var url = UrlInputHelper.ExtractFirstHttpUrl(source);
        if (string.IsNullOrWhiteSpace(url))
        {
            AddLog("请输入或粘贴一个有效的 http/https 地址后再打开浏览器。");
            return;
        }

        string? executablePath;
        try
        {
            executablePath = await _browser.FindInstalledChromiumPathAsync();
        }
        catch (Exception ex)
        {
            AddLog($"查找 Chrome for Testing 失败：{ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(executablePath)
            || !IsPathInsideDirectory(
                executablePath,
                _browser.PreferredChromiumInstallDirectory))
        {
            AddLog(
                $"未找到 HelloLive EXE 目录下的 Chrome for Testing，请先点击“安装 / 更新 Chromium”。安装目录：{_browser.PreferredChromiumInstallDirectory}");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath)
                                   ?? AppContext.BaseDirectory
            };

            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add("--new-window");
            startInfo.ArgumentList.Add(url);

            Process.Start(startInfo);
            AddLog($"已使用 Chrome for Testing 打开：{url}");
        }
        catch (Exception ex)
        {
            AddLog($"打开 Chrome for Testing 失败：{ex.Message}");
        }
    }

    private static bool IsPathInsideDirectory(
        string filePath,
        string directoryPath)
    {
        try
        {
            var fullFilePath = Path.GetFullPath(filePath);
            var fullDirectoryPath = Path.GetFullPath(directoryPath);
            var relative = Path.GetRelativePath(
                fullDirectoryPath,
                fullFilePath);

            return !relative.Equals("..", StringComparison.Ordinal)
                   && !relative.StartsWith(
                       ".." + Path.DirectorySeparatorChar,
                       StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative);
        }
        catch
        {
            return false;
        }
    }

    private async Task AddMonitorAsync()
    {
        var input = NewMonitorUrl.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            AddLog("请输入作者主页或直播分享地址。");
            return;
        }

        var adapter = _platforms.ResolveByInput(input, out var extractedUrl);
        if (adapter is null)
        {
            AddLog("未能从输入内容中识别已支持的平台地址，请检查链接是否正确或该平台是否已启用解析器。");
            return;
        }

        string normalizedUrl;
        try
        {
            normalizedUrl = await adapter.ResolveProfileUrlAsync(extractedUrl);
        }
        catch (Exception ex)
        {
            AddLog($"解析分享链接失败：{ex.Message}");
            return;
        }
        if (Monitors.Any(x => string.Equals(x.ProfileUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
        {
            AddLog("该地址已经在监控列表中。");
            return;
        }

        var hasCustomName = !string.IsNullOrWhiteSpace(NewMonitorName);
        var target = new LiveMonitorTarget
        {
            PlatformId = adapter.Id,
            ProfileUrl = normalizedUrl,
            DisplayName = hasCustomName
                ? NewMonitorName.Trim()
                : BuildDefaultName(normalizedUrl, adapter.DisplayName),
            UseCustomDisplayName = hasCustomName,
            AuthorId = LiveAuthorIdentityHelper.ExtractStableAuthorId(normalizedUrl),
            IsEnabled = true
        };

        AttachTarget(target);
        NewMonitorUrl = string.Empty;
        NewMonitorName = string.Empty;
        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        AddLog($"已添加监控：{target.DisplayName}（{adapter.DisplayName}），正在立即进行无头检查。");

        // Newly-added monitors should resolve nickname/avatar/live state right away.
        // LiveMonitorCoordinator.CheckOneAsync is deliberately forced to headless,
        // so this never depends on the manual browser-mode switch.
        await _coordinator.CheckOneAsync(target.Id);
    }

    public async Task BatchImportMonitorsAsync(string? content)
    {
        var lines = (content ?? string.Empty)
            .Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
        {
            AddLog("批量导入没有发现有效内容；空行会自动忽略。");
            return;
        }

        var addedTargetIds = new List<string>();
        var duplicateCount = 0;
        var invalidCount = 0;
        var failedCount = 0;

        AddLog($"开始批量导入作者，共读取 {lines.Length} 行非空内容。");

        for (var index = 0; index < lines.Length; index++)
        {
            var sourceLine = lines[index];

            var adapter = _platforms.ResolveByInput(
                sourceLine,
                out var extractedUrl);
            if (adapter is null
                || string.IsNullOrWhiteSpace(extractedUrl))
            {
                invalidCount++;
                AddLog(
                    $"批量导入 {index + 1}/{lines.Length}：未识别到受支持的平台地址，已跳过。");
                continue;
            }

            string normalizedUrl;
            try
            {
                normalizedUrl = await adapter.ResolveProfileUrlAsync(
                    extractedUrl);
            }
            catch (Exception ex)
            {
                failedCount++;
                AddLog(
                    $"批量导入 {index + 1}/{lines.Length}：解析地址失败，已跳过 - {ex.Message}");
                continue;
            }

            if (Monitors.Any(x =>
                    string.Equals(
                        x.ProfileUrl,
                        normalizedUrl,
                        StringComparison.OrdinalIgnoreCase)))
            {
                duplicateCount++;
                AddLog(
                    $"批量导入 {index + 1}/{lines.Length}：该作者已在监控列表中，已跳过。");
                continue;
            }

            var target = new LiveMonitorTarget
            {
                PlatformId = adapter.Id,
                ProfileUrl = normalizedUrl,
                DisplayName = BuildDefaultName(
                    normalizedUrl,
                    adapter.DisplayName),
                UseCustomDisplayName = false,
                AuthorId = LiveAuthorIdentityHelper.ExtractStableAuthorId(
                    normalizedUrl),
                IsEnabled = true
            };

            AttachTarget(target);
            addedTargetIds.Add(target.Id);

            AddLog(
                $"批量导入 {index + 1}/{lines.Length}：已加入监控列表（{adapter.DisplayName}）→ {normalizedUrl}");
        }

        if (addedTargetIds.Count > 0)
        {
            await SaveMonitorsAsync();
            RefreshCoordinatorState();
            RaiseMetricsChanged();

            // Do not make the import dialog wait for every Playwright check.
            // The cards appear immediately, then nickname/avatar/live state are
            // populated in the background using the same single-item check path.
            _ = RecheckBatchImportedMonitorsAsync(
                addedTargetIds.ToArray());
        }

        AddLog(
            $"批量导入完成：新增 {addedTargetIds.Count} 个，重复 {duplicateCount} 个，无效 {invalidCount} 个，解析失败 {failedCount} 个，共 {lines.Length} 个。");
    }

    private async Task RecheckBatchImportedMonitorsAsync(
        IReadOnlyList<string> targetIds)
    {
        foreach (var targetId in targetIds)
        {
            var target = Monitors.FirstOrDefault(x =>
                string.Equals(
                    x.Id,
                    targetId,
                    StringComparison.Ordinal));

            if (target is null || !target.IsEnabled)
                continue;

            try
            {
                await _coordinator.CheckOneAsync(target.Id);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    AddLog(
                        $"{target.DisplayName}：批量导入后的后台检查失败 - {ex.Message}"));
            }
        }
    }

    public async Task<string?> UpdateMonitorAuthorInfoAsync(
        LiveMonitorTarget target,
        string displayName,
        string avatarUrl,
        string monitorUrl)
    {
        ArgumentNullException.ThrowIfNull(target);

        var newName = (displayName ?? string.Empty).Trim();
        var newAvatarUrl = (avatarUrl ?? string.Empty).Trim();
        var input = (monitorUrl ?? string.Empty).Trim();

        var useAutoDisplayName = string.IsNullOrWhiteSpace(newName);

        if (!string.IsNullOrWhiteSpace(newAvatarUrl)
            && (!Uri.TryCreate(newAvatarUrl, UriKind.Absolute, out var avatarUri)
                || avatarUri.Scheme is not ("http" or "https")))
        {
            return "头像 URL 必须是有效的 http/https 地址，或留空。";
        }

        if (string.IsNullOrWhiteSpace(input))
            return "请输入监控 URL。";

        var adapter = _platforms.ResolveByInput(input, out var extractedUrl);
        if (adapter is null)
            return "未能从监控 URL 中识别受支持的平台地址。";

        string normalizedUrl;
        try
        {
            normalizedUrl = await adapter.ResolveProfileUrlAsync(extractedUrl);
        }
        catch (Exception ex)
        {
            return $"解析监控 URL 失败：{ex.Message}";
        }

        if (Monitors.Any(x => !ReferenceEquals(x, target)
                              && string.Equals(
                                  x.ProfileUrl,
                                  normalizedUrl,
                                  StringComparison.OrdinalIgnoreCase)))
        {
            return "该监控 URL 已经在列表中。";
        }

        var urlChanged = !string.Equals(
            target.ProfileUrl,
            normalizedUrl,
            StringComparison.OrdinalIgnoreCase);
        var avatarChanged = !string.Equals(
            target.AvatarUrl ?? string.Empty,
            newAvatarUrl,
            StringComparison.Ordinal);

        if (urlChanged)
        {
            await _coordinator.StopRecordingAsync(target.Id);
            _loadedAvatarUrls.Remove(target.Id);
            _lastConfirmedLiveStates.Remove(target.Id);

            target.PlatformId = adapter.Id;
            target.ProfileUrl = normalizedUrl;
            target.AuthorId = LiveAuthorIdentityHelper.ExtractStableAuthorId(normalizedUrl);
            target.State = LiveMonitorState.Idle;
            target.StatusMessage = "作者信息已修改，等待重新检查";
            target.LastCheckedAt = null;
            target.StreamUrl = null;
            target.StreamFormat = null;
            target.ResolvedPageUrl = null;
            target.IsRecording = false;
            target.RecordingFilePath = null;
            target.RecordingStatus = string.Empty;
        }

        target.UseCustomDisplayName = !useAutoDisplayName;
        if (useAutoDisplayName)
        {
            // 留空表示恢复自动昵称。先显示一个稳定的 URL 派生名称，
            // 下一次无头检查拿到作者资料后会自动覆盖为最新昵称。
            target.DisplayName = BuildDefaultName(normalizedUrl, adapter.DisplayName);
        }
        else
        {
            target.DisplayName = newName;
        }

        target.UseCustomAvatar = !string.IsNullOrWhiteSpace(newAvatarUrl);
        target.AvatarUrl = target.UseCustomAvatar
            ? newAvatarUrl
            : null;

        if (avatarChanged)
        {
            _loadedAvatarUrls.Remove(target.Id);
            target.AvatarImage = null;
            _ = LoadMonitorAvatarAsync(target);
        }

        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        AddLog($"已修改作者信息：{target.DisplayName}。");

        if ((urlChanged || useAutoDisplayName) && target.IsEnabled)
        {
            target.StatusMessage = "作者信息已保存，正在后台重新获取作者信息";
            _ = RecheckMonitorAfterEditAsync(target.Id, target.DisplayName);
        }

        return null;
    }

    private async Task RecheckMonitorAfterEditAsync(
        string targetId,
        string displayName)
    {
        try
        {
            await _coordinator.CheckOneAsync(targetId);
        }
        catch (Exception ex)
        {
            AddLog($"{displayName}：修改作者信息后的后台检查失败 - {ex.Message}");
        }
    }

    private async Task RemoveMonitorAsync(LiveMonitorTarget? target)
    {
        if (target is null)
            return;

        await _coordinator.StopRecordingAsync(target.Id);
        _loadedAvatarUrls.Remove(target.Id);
        _lastConfirmedLiveStates.Remove(target.Id);
        _folderSizeRefreshVersions.TryRemove(target.Id, out _);
        target.PropertyChanged -= Target_PropertyChanged;

        if (ReferenceEquals(CurrentStatusTarget, target))
            CurrentStatusTarget = null;

        Monitors.Remove(target);
        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        AddLog($"已移除监控：{target.DisplayName}。");
    }

    private async Task CheckTargetAsync(LiveMonitorTarget? target)
    {
        if (target is null)
            return;

        RefreshCoordinatorState();
        await _coordinator.CheckOneAsync(target.Id);
    }

    private async Task CheckAllAsync()
    {
        RefreshCoordinatorState();
        await _coordinator.CheckAllOnceAsync();
    }

    private async Task StartMonitoringAsync()
    {
        RefreshCoordinatorState();
        await _coordinator.StartAsync();
    }

    private async Task StopMonitoringAsync()
    {
        await _coordinator.StopAsync();

        // Recorder normally publishes IsRecording=false for every completed session.
        // Reconcile the UI explicitly as well so a queued/delayed recorder event can
        // never leave a stale "正在录制" badge after monitoring has stopped.
        foreach (var target in Monitors.Where(x => x.IsRecording))
            ApplyStoppedRecordingState(target, "监控已停止，录像已结束");
    }

    private async Task InstallChromiumAsync()
    {
        if (IsChromiumInstalling)
            return;

        IsChromiumInstalling = true;
        IsChromiumInstallProgressVisible = true;
        IsChromiumInstallProgressIndeterminate = true;
        ChromiumInstallProgressText = "准备安装 Chromium…";
        AddLog("开始安装与 Microsoft.Playwright 1.61.0 匹配的 Chromium。");

        try
        {
            var progress = new Progress<ChromiumInstallProgress>(value =>
            {
                IsChromiumInstallProgressIndeterminate = value.Percent is null;
                ChromiumInstallProgressPercent = value.Percent ?? 0;
                ChromiumInstallProgressText = value.Percent is { } percent
                    ? $"{value.Stage} {percent:0.#}%"
                    : value.Stage;
            });

            var exitCode = await _browser.InstallChromiumAsync(progress);
            if (exitCode != 0)
                throw new InvalidOperationException($"Playwright 安装器退出代码：{exitCode}");

            var path = await _browser.FindInstalledChromiumPathAsync();
            BrowserStatusText = string.IsNullOrWhiteSpace(path)
                ? "安装命令已完成，但仍未找到 Chromium。"
                : $"Chromium：{path}";
            AddLog("Chromium 安装完成。");
        }
        catch (Exception ex)
        {
            BrowserStatusText = "Chromium 安装失败";
            AddLog($"Chromium 安装失败：{ex.Message}");
        }
        finally
        {
            IsChromiumInstalling = false;
            IsChromiumInstallProgressIndeterminate = false;
        }
    }

    private async Task InstallFfmpegAsync()
    {
        if (IsFfmpegInstalling || !IsFfmpegAutoInstallSupported)
            return;

        IsFfmpegInstalling = true;
        IsFfmpegInstallProgressVisible = true;
        IsFfmpegInstallProgressIndeterminate = true;
        FfmpegInstallProgressPercent = 0;
        FfmpegInstallProgressText = "准备下载 FFmpeg…";
        AddLog($"开始下载 FFmpeg，安装目录：{_ffmpegInstaller.InstallDirectory}");

        var installSucceeded = false;
        try
        {
            var progress = new Progress<FfmpegInstallProgress>(value =>
            {
                IsFfmpegInstallProgressIndeterminate = value.Percentage is null;
                FfmpegInstallProgressPercent = value.Percentage ?? 0;

                var sizeText = value.TotalBytes is > 0
                    ? $" · {FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes.Value)}"
                    : value.BytesReceived > 0
                        ? $" · {FormatBytes(value.BytesReceived)}"
                        : string.Empty;
                var speedText = value.BytesPerSecond > 0
                    ? $" · {FormatBytes((long)value.BytesPerSecond)}/s"
                    : string.Empty;

                FfmpegInstallProgressText =
                    value.Percentage is { } percentage
                        ? $"{value.Message} {percentage}%{sizeText}{speedText}"
                        : $"{value.Message}{sizeText}{speedText}";
            });

            var result = await _ffmpegInstaller.InstallAsync(progress);
            FfmpegStatusText = $"FFmpeg 已可用：{Path.GetDirectoryName(result.FfmpegPath)}";
            FfmpegInstallProgressText = "FFmpeg 安装完成，无需重启 HelloLive。";
            FfmpegInstallProgressPercent = 100;
            installSucceeded = true;
            AddLog($"FFmpeg 安装完成：{result.FfmpegPath}");
        }
        catch (Exception ex)
        {
            FfmpegStatusText = "FFmpeg 下载或安装失败";
            FfmpegInstallProgressText = ex.Message;
            AddLog($"FFmpeg 下载或安装失败：{ex.Message}");
        }
        finally
        {
            IsFfmpegInstalling = false;
            IsFfmpegInstallProgressIndeterminate = false;
            RefreshFfmpegStatus();

            // 成功后收起进度区，只保留“已可用”的状态文本；
            // 失败时继续显示错误详情，方便用户判断原因。
            if (installSucceeded)
                IsFfmpegInstallProgressVisible = false;
        }
    }

    private void RefreshFfmpegStatus()
    {
        var info = _ffmpegInstaller.GetToolInfo();
        FfmpegStatusText = info.IsFound && !string.IsNullOrWhiteSpace(info.FfmpegPath)
            ? $"FFmpeg 已可用：{Path.GetDirectoryName(info.FfmpegPath)}"
            : IsFfmpegAutoInstallSupported
                ? $"尚未检测到 FFmpeg。可下载到：{Path.Combine(_ffmpegInstaller.InstallDirectory, "bin")}"
                : "当前系统不支持程序内自动下载 FFmpeg，请通过系统包管理器安装。";

        OnPropertyChanged(nameof(InstallFfmpegButtonText));
        InstallFfmpegCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.0} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private void Coordinator_CheckResultChanged(LiveCheckResult result)
    {
        void ApplyResultOnUi()
        {
            var target = Monitors.FirstOrDefault(x => x.Id == result.TargetId);
            if (target is null)
                return;

            CurrentStatusTarget = target;
            target.ApplyResult(result);

            if (result.State != LiveMonitorState.Checking
                && !string.IsNullOrWhiteSpace(target.AuthorId))
            {
                target = ReconcileDuplicateAuthor(target, result);
                CurrentStatusTarget = target;
            }

            _ = LoadMonitorAvatarAsync(target);
            HandleLiveTransitionForPushPlus(target, result);
            CurrentTask = result.State == LiveMonitorState.Checking
                ? $"正在检查：{target.DisplayName}"
                : $"{target.DisplayName} · {target.StateText}";
            RaiseMetricsChanged();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyResultOnUi();
        }
        else
        {
            Dispatcher.UIThread
                .InvokeAsync(ApplyResultOnUi)
                .GetAwaiter()
                .GetResult();
        }
    }

    private LiveMonitorTarget ReconcileDuplicateAuthor(
        LiveMonitorTarget checkedTarget,
        LiveCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(checkedTarget.AuthorId))
            return checkedTarget;

        var matches = Monitors
            .Where(item =>
                string.Equals(
                    item.PlatformId,
                    checkedTarget.PlatformId,
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.AuthorId)
                && string.Equals(
                    item.AuthorId,
                    checkedTarget.AuthorId,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length <= 1)
            return checkedTarget;

        // Preserve the first item in the user's list. Alternate profile/live URLs
        // can initially look different and only become identifiable after polling
        // resolves the platform's real author id.
        var canonical = matches
            .OrderBy(item => Monitors.IndexOf(item))
            .First();

        if (!ReferenceEquals(canonical, checkedTarget))
        {
            canonical.ApplyResult(result with { TargetId = canonical.Id });
            _ = LoadMonitorAvatarAsync(canonical);
        }

        foreach (var duplicate in matches)
        {
            if (ReferenceEquals(duplicate, canonical))
                continue;

            // Preserve useful user choices from the duplicate before removing it.
            if (duplicate.IsEnabled && !canonical.IsEnabled)
                canonical.IsEnabled = true;

            if (duplicate.UseCustomDisplayName
                && !canonical.UseCustomDisplayName
                && !string.IsNullOrWhiteSpace(duplicate.DisplayName))
            {
                canonical.UseCustomDisplayName = true;
                canonical.DisplayName = duplicate.DisplayName;
            }

            if (duplicate.UseCustomAvatar
                && !canonical.UseCustomAvatar
                && !string.IsNullOrWhiteSpace(duplicate.AvatarUrl))
            {
                canonical.UseCustomAvatar = true;
                canonical.AvatarUrl = duplicate.AvatarUrl;
            }

            duplicate.PropertyChanged -= Target_PropertyChanged;
            _loadedAvatarUrls.Remove(duplicate.Id);
            _lastConfirmedLiveStates.Remove(duplicate.Id);
            _folderSizeRefreshVersions.TryRemove(duplicate.Id, out _);

            if (ReferenceEquals(CurrentStatusTarget, duplicate))
                CurrentStatusTarget = canonical;

            Monitors.Remove(duplicate);
            _ = _coordinator.StopRecordingAsync(duplicate.Id);

            AddLog(
                $"检测到重复作者并自动去重：{duplicate.DisplayName} 与 {canonical.DisplayName} 的作者ID均为 {canonical.AuthorId}，已保留较早加入的一项。");
        }

        RefreshCoordinatorState();
        RaiseMetricsChanged();
        _ = SaveMonitorsAsync();

        return canonical;
    }

    private void HandleLiveTransitionForPushPlus(
        LiveMonitorTarget target,
        LiveCheckResult result)
    {
        if (result.State == LiveMonitorState.Checking)
            return;

        if (result.State == LiveMonitorState.NotDetected)
        {
            _lastConfirmedLiveStates[target.Id] = false;
            return;
        }

        if (result.State != LiveMonitorState.Live)
            return;

        var wasLive = _lastConfirmedLiveStates.TryGetValue(
            target.Id,
            out var previous)
            && previous;
        _lastConfirmedLiveStates[target.Id] = true;

        if (wasLive || string.IsNullOrWhiteSpace(PushPlusToken))
            return;

        _ = SendLiveStartedPushPlusAsync(target);
    }

    private async Task SendLiveStartedPushPlusAsync(LiveMonitorTarget target)
    {
        try
        {
            await _pushPlusNotification.SendLiveStartedAsync(
                PushPlusToken,
                target);
            Dispatcher.UIThread.Post(() =>
                AddLog($"PushPlus 开播提醒已发送：{target.DisplayName}"));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                AddLog($"PushPlus 开播提醒发送失败：{ex.Message}"));
        }
    }

    private void Coordinator_RecordingStateChanged(LiveRecordingState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = Monitors.FirstOrDefault(x => x.Id == state.TargetId);
            if (target is null)
                return;

            // A delayed "recording started" notification must not resurrect the
            // recording badge after global monitoring was stopped or this item
            // was disabled. Stop notifications are always accepted.
            if (state.IsRecording
                && (!_coordinator.IsRunning || !target.IsEnabled))
            {
                return;
            }

            target.ApplyRecordingState(state);
        });
    }

    private void Coordinator_StorageChanged(string targetId)
        => Dispatcher.UIThread.Post(() =>
        {
            var target = Monitors.FirstOrDefault(x =>
                string.Equals(x.Id, targetId, StringComparison.Ordinal));
            if (target is not null)
                QueueMonitorFolderSizeRefresh(target);
        });

    private void Coordinator_LowDiskSpaceDetected(LowDiskSpaceInfo info)
        => Dispatcher.UIThread.Post(() =>
        {
            _pendingLowDiskSpaceAlert = info;
            AddLog(
                $"磁盘空间不足：录像目录所在磁盘剩余 {FormatBytes(info.AvailableBytes)}，已达到 1 GB 停止阈值，所有录像已停止。");
            LowDiskSpaceAlertRequested?.Invoke(info);
        });

    private void QueueMonitorFolderSizeRefresh(LiveMonitorTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.AuthorId)
            && string.IsNullOrWhiteSpace(target.RecordingFilePath))
        {
            target.RecordingFolderSizeBytes = null;
            return;
        }

        var version = _folderSizeRefreshVersions.AddOrUpdate(
            target.Id,
            1,
            static (_, current) => current + 1);

        _ = RefreshMonitorFolderSizeAsync(target.Id, version);
    }

    private async Task RefreshMonitorFolderSizeAsync(
        string targetId,
        int version)
    {
        await _folderSizeGate.WaitAsync();
        try
        {
            if (!_folderSizeRefreshVersions.TryGetValue(
                    targetId,
                    out var latestVersion)
                || latestVersion != version)
            {
                return;
            }

            var target = await Dispatcher.UIThread.InvokeAsync(() =>
                Monitors.FirstOrDefault(x =>
                    string.Equals(
                        x.Id,
                        targetId,
                        StringComparison.Ordinal)));

            if (target is null)
                return;

            string folderPath;
            try
            {
                folderPath = GetMonitorFolderPath(target);
            }
            catch
            {
                return;
            }

            var size = await Task.Run(() =>
                CalculateDirectorySize(folderPath));

            if (!_folderSizeRefreshVersions.TryGetValue(
                    targetId,
                    out latestVersion)
                || latestVersion != version)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var current = Monitors.FirstOrDefault(x =>
                    string.Equals(
                        x.Id,
                        targetId,
                        StringComparison.Ordinal));
                if (current is not null)
                    current.RecordingFolderSizeBytes = size;
            });
        }
        finally
        {
            _folderSizeGate.Release();
        }
    }

    private static long CalculateDirectorySize(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)
            || !Directory.Exists(folderPath))
        {
            return 0;
        }

        long total = 0;

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(
                         folderPath,
                         "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(filePath).Length;
                }
                catch
                {
                    // A file can disappear while compression/cleanup is running.
                }
            }
        }
        catch
        {
            // Keep the last readable total instead of affecting the monitor UI.
        }

        return total;
    }

    private void Coordinator_Log(string message)
        => Dispatcher.UIThread.Post(() => AddLog(message));

    private void Coordinator_RunningChanged(bool isRunning)
        => Dispatcher.UIThread.Post(() => IsMonitoring = isRunning);

    private void AttachTarget(LiveMonitorTarget target)
    {
        // Older builds stored Kuaishou /u/{principalId} or /profile/{principalId}
        // as AuthorId. That is not the account author.id used by HelloCrab folders.
        if (LiveAuthorIdentityHelper.IsKuaishouPageIdentifier(
                target.ProfileUrl,
                target.AuthorId))
        {
            target.AuthorId = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(target.AuthorId))
        {
            target.AuthorId = LiveAuthorIdentityHelper.ExtractStableAuthorId(
                target.ProfileUrl);
        }

        target.PropertyChanged += Target_PropertyChanged;
        Monitors.Add(target);
        _ = LoadMonitorAvatarAsync(target);
        QueueMonitorFolderSizeRefresh(target);
    }

    private async Task LoadMonitorAvatarAsync(LiveMonitorTarget target)
    {
        var url = target.AvatarUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (_loadedAvatarUrls.TryGetValue(target.Id, out var loadedUrl)
            && string.Equals(loadedUrl, url, StringComparison.Ordinal)
            && target.AvatarImage is not null)
        {
            return;
        }

        var image = await _imageCache.LoadAsync(url);
        if (image is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!string.Equals(target.AvatarUrl, url, StringComparison.Ordinal))
                return;

            target.AvatarImage = image;
            _loadedAvatarUrls[target.Id] = url;
        });
    }

    private void Target_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LiveMonitorTarget target)
            return;

        if (e.PropertyName is nameof(LiveMonitorTarget.IsEnabled)
            or nameof(LiveMonitorTarget.DisplayName)
            or nameof(LiveMonitorTarget.AuthorId)
            or nameof(LiveMonitorTarget.AvatarUrl)
            or nameof(LiveMonitorTarget.ProfileUrl))
        {
            if (e.PropertyName == nameof(LiveMonitorTarget.IsEnabled)
                && !target.IsEnabled)
            {
                _lastConfirmedLiveStates.Remove(target.Id);
                _ = StopRecordingAndResetAsync(target);
            }

            RefreshCoordinatorState();
            RaiseMetricsChanged();
            OnPropertyChanged(nameof(FilteredMonitors));

            if (e.PropertyName is nameof(LiveMonitorTarget.DisplayName)
                or nameof(LiveMonitorTarget.AuthorId)
                or nameof(LiveMonitorTarget.ProfileUrl))
            {
                QueueMonitorFolderSizeRefresh(target);
            }

            _ = SaveMonitorsAsync();
        }
    }

    private async Task StopRecordingAndResetAsync(
        LiveMonitorTarget target)
    {
        try
        {
            await _coordinator.StopRecordingAsync(target.Id);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The item might have been removed while the recorder was closing.
                if (!Monitors.Contains(target))
                    return;

                if (target.IsRecording)
                    ApplyStoppedRecordingState(target, "监控项已停用，录像已结束");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                AddLog($"{target.DisplayName}：停止录像失败 - {ex.Message}"));
        }
    }

    private static void ApplyStoppedRecordingState(
        LiveMonitorTarget target,
        string message)
    {
        var fileName = target.RecordingFileName;
        var status = string.IsNullOrWhiteSpace(fileName)
            ? message
            : $"{message}：{fileName}";

        target.ApplyRecordingState(new LiveRecordingState(
            target.Id,
            false,
            target.RecordingFilePath,
            status,
            DateTimeOffset.Now));
    }

    private void RefreshCoordinatorState()
    {
        _coordinator.SetTargets(Monitors.Select(x => x.ToSnapshot()));
        _coordinator.SetOptions(new LiveMonitorOptions(
            HeadlessMode,
            MaxConcurrentPages,
            CheckIntervalSeconds,
            CheckTimeoutSeconds,
            BlockImagesAndFonts));
        _coordinator.SetPostRecordingCompressionOptions(
            new PostRecordingCompressionOptions(
                CompressAfterRecording,
                DeleteOriginalAfterCompression));
    }

    private void RaiseMetricsChanged()
    {
        OnPropertyChanged(nameof(MonitorCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(LiveCount));
        OnPropertyChanged(nameof(NotDetectedCount));
        OnPropertyChanged(nameof(CheckingCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(FilteredMonitors));
    }

    private void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        // Newest first: the latest runtime event is always visible at the top,
        // so users do not need to keep scrolling to the bottom while monitoring.
        Logs.Insert(0, line);
        while (Logs.Count > 500)
            Logs.RemoveAt(Logs.Count - 1);
    }

    public void AddRemoteLog(string message) => AddLog(message);

    public void AddBackgroundLog(string message)
        => Dispatcher.UIThread.Post(() => AddLog(message));

    public void SetRemoteApiStatusText(string text)
        => RemoteApiStatusText = string.IsNullOrWhiteSpace(text)
            ? "远程控制服务器未启动"
            : text;

    public RemoteLiveSnapshot CreateRemoteSnapshot()
        => new()
        {
            ServerTime = DateTimeOffset.Now,
            IsMonitoring = IsMonitoring,
            CurrentTask = CurrentTask,
            MonitorCount = MonitorCount,
            EnabledCount = EnabledCount,
            LiveCount = LiveCount,
            NotDetectedCount = NotDetectedCount,
            CheckingCount = CheckingCount,
            FailedCount = FailedCount,
            Monitors = Monitors.Select(item => new RemoteMonitorDto
            {
                Id = item.Id,
                PlatformId = item.PlatformId,
                PlatformText = item.PlatformText,
                DisplayName = item.DisplayName,
                AuthorId = item.AuthorId,
                ProfileUrl = item.ProfileUrl,
                IsEnabled = item.IsEnabled,
                StateText = item.StateText,
                StatusMessage = item.StatusMessage,
                LastCheckedText = item.LastCheckedText,
                StreamFormat = item.StreamFormat,
                StreamUrl = item.StreamUrl,
                IsRecording = item.IsRecording,
                RecordingFilePath = item.RecordingFilePath,
                RecordingStatus = item.RecordingStatus
            }).ToList(),
            Logs = Logs.Take(120).ToList()
        };

    public Task StartRemoteMonitoringAsync() => StartMonitoringAsync();

    public Task StopRemoteMonitoringAsync() => StopMonitoringAsync();

    public Task CheckAllRemoteAsync() => CheckAllAsync();

    public async Task<string> AddRemoteMonitorAsync(string url, string? name)
    {
        var input = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
            return "请输入作者主页或直播分享地址。";

        var adapter = _platforms.ResolveByInput(input, out var extractedUrl);
        if (adapter is null)
            return "未能从输入内容中识别受支持的作者主页或分享链接。";

        string normalizedUrl;
        try
        {
            normalizedUrl = await adapter.ResolveProfileUrlAsync(extractedUrl);
        }
        catch (Exception ex)
        {
            return $"解析分享链接失败：{ex.Message}";
        }
        if (Monitors.Any(x => string.Equals(x.ProfileUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
            return "该地址已经在监控列表中。";

        var hasCustomName = !string.IsNullOrWhiteSpace(name);
        var target = new LiveMonitorTarget
        {
            PlatformId = adapter.Id,
            ProfileUrl = normalizedUrl,
            DisplayName = hasCustomName
                ? name!.Trim()
                : BuildDefaultName(normalizedUrl, adapter.DisplayName),
            UseCustomDisplayName = hasCustomName,
            AuthorId = LiveAuthorIdentityHelper.ExtractStableAuthorId(normalizedUrl),
            IsEnabled = true
        };

        AttachTarget(target);
        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        AddLog($"远程添加监控：{target.DisplayName}（{adapter.DisplayName}）。");
        return $"已添加监控：{target.DisplayName}";
    }

    public async Task<string> RemoveRemoteMonitorAsync(string id)
    {
        var target = Monitors.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (target is null)
            return "未找到对应的监控对象。";

        await _coordinator.StopRecordingAsync(target.Id);
        _loadedAvatarUrls.Remove(target.Id);
        _lastConfirmedLiveStates.Remove(target.Id);
        target.PropertyChanged -= Target_PropertyChanged;
        Monitors.Remove(target);
        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        AddLog($"远程移除监控：{target.DisplayName}。");
        return $"已移除监控：{target.DisplayName}";
    }

    public async Task<string> SetRemoteMonitorEnabledAsync(string id, bool enabled)
    {
        var target = Monitors.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (target is null)
            return "未找到对应的监控对象。";

        target.IsEnabled = enabled;
        if (!enabled)
            await _coordinator.StopRecordingAsync(target.Id);
        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        return enabled
            ? $"已启用监控：{target.DisplayName}"
            : $"已停用监控：{target.DisplayName}";
    }

    public async Task<string> CheckRemoteMonitorAsync(string id)
    {
        var target = Monitors.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (target is null)
            return "未找到对应的监控对象。";

        RefreshCoordinatorState();
        await _coordinator.CheckOneAsync(target.Id);
        return $"已完成检查：{target.DisplayName}";
    }

    private void ToggleTheme()
    {
        _settings.Theme = string.Equals(_settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        ApplyTheme(_settings.Theme);
        PersistSettingsSoon();
    }

    private void ToggleMonitorPanel()
    {
        IsMonitorPanelVisible = !IsMonitorPanelVisible;
    }

    private void ToggleMonitorGrid()
    {
        if (!IsMonitorPanelVisible)
            IsMonitorPanelVisible = true;

        IsMonitorGridExpanded = !IsMonitorGridExpanded;
    }

    private void ApplyTheme(string theme)
    {
        var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ThemeIcon = dark ? "☀" : "☾";
    }

    private void PersistSettingsSoon()
        => _ = _settingsService.SaveAsync(_settings);

    private async Task SaveMonitorsAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            await _monitorStore.SaveAsync(Monitors);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public string GetMonitorFolderPath(LiveMonitorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // If this monitor has already recorded, the recorder's actual path is authoritative.
        if (!string.IsNullOrWhiteSpace(target.RecordingFilePath))
        {
            var existingDirectory = Path.GetDirectoryName(target.RecordingFilePath);
            if (!string.IsNullOrWhiteSpace(existingDirectory))
                return existingDirectory;
        }

        var platformRoot = Path.Combine(
            DownloadRoot,
            PlatformFolderHelper.GetFolderName(target.PlatformId));

        // A real author id is required for HelloCrab-compatible author folders.
        // Before it is resolved, open the platform folder rather than creating a wrong
        // folder from Kuaishou's page/principal id.
        if (string.IsNullOrWhiteSpace(target.AuthorId))
            return platformRoot;

        return AuthorFolderResolver.Resolve(
            platformRoot,
            target.DisplayName,
            target.AuthorId);
    }

    private static string BuildDefaultName(string url, string platformDisplayName)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(segment))
                return segment;
        }

        return platformDisplayName + "监控";
    }

    private static bool LooksLikeAutoDisplayName(string? displayName, string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(displayName)
            || !Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return !string.IsNullOrWhiteSpace(segment)
               && string.Equals(
                   displayName.Trim(),
                   Uri.UnescapeDataString(segment),
                   StringComparison.Ordinal);
    }

    public async Task PrepareForShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownPrepared, 1) != 0)
            return;

        AddLog("正在安全停止直播录像并刷新文件…");
        await _coordinator.StopAsync();
        await _settingsService.SaveAsync(_settings);
        await SaveMonitorsAsync();
        AddLog("直播录像已安全停止。");
    }

    public async ValueTask DisposeAsync()
    {
        await PrepareForShutdownAsync();

        _coordinator.CheckResultChanged -= Coordinator_CheckResultChanged;
        _coordinator.RecordingStateChanged -= Coordinator_RecordingStateChanged;
        _coordinator.StorageChanged -= Coordinator_StorageChanged;
        _coordinator.LowDiskSpaceDetected -= Coordinator_LowDiskSpaceDetected;
        _coordinator.Log -= Coordinator_Log;
        _coordinator.RunningChanged -= Coordinator_RunningChanged;

        foreach (var target in Monitors)
            target.PropertyChanged -= Target_PropertyChanged;

        await _coordinator.DisposeAsync();
        await _settingsService.SaveAsync(_settings);
        await SaveMonitorsAsync();
        _pushPlusNotification.Dispose();
        if (_ffmpegInstaller is IDisposable ffmpegDisposable)
            ffmpegDisposable.Dispose();
        _imageCache.Dispose();
        // Folder-size scans are fire-and-forget background work and may still be
        // unwinding during shutdown; leave this lightweight semaphore undisposed
        // to avoid a late Release() racing with disposal.
        _saveGate.Dispose();
    }
}
