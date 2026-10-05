using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelloLive.Core.Contracts;
using HelloLive.Core.Models;
using HelloLive.Core.Services.Browser;
using HelloLive.Core.Services.Monitoring;
using HelloLive.Core.Services.Settings;
using HelloLive.Core.Sites;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace HelloLive.Core.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILiveBrowserService _browser;
    private readonly LivePlatformRegistry _platforms;
    private readonly LiveMonitorCoordinator _coordinator;
    private readonly SettingsService _settingsService;
    private readonly MonitorStore _monitorStore;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

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
    private string _currentTask = "等待任务";
    private string _browserStatusText = "尚未检查 Chromium";
    private bool _isChromiumInstalling;
    private bool _isChromiumInstallProgressVisible;
    private bool _isChromiumInstallProgressIndeterminate;
    private double _chromiumInstallProgressPercent;
    private string _chromiumInstallProgressText = string.Empty;
    private string _themeIcon = "☾";
    private bool _remoteApiEnabled;
    private int _remoteApiPort;
    private string _remoteApiToken = string.Empty;
    private string _remoteApiStatusText = "远程控制服务器未启动";

    public MainWindowViewModel(
        ILiveBrowserService browser,
        LivePlatformRegistry platforms,
        LiveMonitorCoordinator coordinator,
        SettingsService settingsService,
        MonitorStore monitorStore)
    {
        _browser = browser;
        _platforms = platforms;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _monitorStore = monitorStore;
        _settings = settingsService.Load();

        _headlessMode = _settings.HeadlessMode;
        _maxConcurrentPages = _settings.MaxConcurrentPages;
        _checkIntervalSeconds = _settings.CheckIntervalSeconds;
        _checkTimeoutSeconds = _settings.CheckTimeoutSeconds;
        _blockImagesAndFonts = _settings.BlockImagesAndFonts;
        _autoStartMonitoring = _settings.AutoStartMonitoring;
        _isMonitorPanelVisible = _settings.MonitorPanelVisible;
        _remoteApiEnabled = _settings.RemoteApiEnabled;
        _remoteApiPort = _settings.RemoteApiPort;
        _remoteApiToken = _settings.RemoteApiToken;

        AddMonitorCommand = new AsyncRelayCommand(AddMonitorAsync);
        StartMonitoringCommand = new AsyncRelayCommand(StartMonitoringAsync);
        StopMonitoringCommand = new AsyncRelayCommand(StopMonitoringAsync);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync);
        InstallChromiumCommand = new AsyncRelayCommand(InstallChromiumAsync, () => !IsChromiumInstalling);
        CheckTargetCommand = new AsyncRelayCommand<LiveMonitorTarget>(CheckTargetAsync);
        RemoveMonitorCommand = new AsyncRelayCommand<LiveMonitorTarget>(RemoveMonitorAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleMonitorPanelCommand = new RelayCommand(ToggleMonitorPanel);

        foreach (var target in _monitorStore.Load())
            AttachTarget(target);

        _coordinator.CheckResultChanged += Coordinator_CheckResultChanged;
        _coordinator.Log += Coordinator_Log;
        _coordinator.RunningChanged += Coordinator_RunningChanged;

        ApplyTheme(_settings.Theme);
        RefreshCoordinatorState();
        RaiseMetricsChanged();
    }

    public event EventHandler<bool>? RemoteApiEnabledChanged;
    public event EventHandler? RemoteApiPortChanged;

    public ObservableCollection<LiveMonitorTarget> Monitors { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public IReadOnlyList<PlatformOption> Platforms => _platforms.Platforms;

    public IAsyncRelayCommand AddMonitorCommand { get; }
    public IAsyncRelayCommand StartMonitoringCommand { get; }
    public IAsyncRelayCommand StopMonitoringCommand { get; }
    public IAsyncRelayCommand CheckAllCommand { get; }
    public IAsyncRelayCommand InstallChromiumCommand { get; }
    public IAsyncRelayCommand<LiveMonitorTarget> CheckTargetCommand { get; }
    public IAsyncRelayCommand<LiveMonitorTarget> RemoveMonitorCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IRelayCommand ToggleMonitorPanelCommand { get; }

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
            _settings.MonitorPanelVisible = value;
            PersistSettingsSoon();
        }
    }

    public string CurrentTask
    {
        get => _currentTask;
        private set => SetProperty(ref _currentTask, value);
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

    public string RemoteApiToken => _remoteApiToken;

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

    public async Task InitializeAsync()
    {
        var path = await _browser.FindInstalledChromiumPathAsync();
        BrowserStatusText = string.IsNullOrWhiteSpace(path)
            ? $"未发现 Chromium，可点击安装。安装目录：{_browser.PreferredChromiumInstallDirectory}"
            : $"Chromium：{path}";

        AddLog($"已加载 {Monitors.Count} 个监控对象。当前最大并发页面数：{MaxConcurrentPages}。 ");

        if (AutoStartMonitoring && Monitors.Any(x => x.IsEnabled))
            await StartMonitoringAsync();
    }

    private async Task AddMonitorAsync()
    {
        var input = NewMonitorUrl.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            AddLog("请输入作者主页或直播分享地址。");
            return;
        }

        var adapter = _platforms.ResolveByProfileUrl(input);
        if (adapter is null)
        {
            AddLog("暂不支持这个地址。当前版本已实现快手适配器，其他平台可通过接口继续扩展。");
            return;
        }

        var normalizedUrl = adapter.NormalizeProfileUrl(input);
        if (Monitors.Any(x => string.Equals(x.ProfileUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
        {
            AddLog("该地址已经在监控列表中。");
            return;
        }

        var target = new LiveMonitorTarget
        {
            PlatformId = adapter.Id,
            ProfileUrl = normalizedUrl,
            DisplayName = string.IsNullOrWhiteSpace(NewMonitorName)
                ? BuildDefaultName(normalizedUrl, adapter.DisplayName)
                : NewMonitorName.Trim(),
            IsEnabled = true
        };

        AttachTarget(target);
        NewMonitorUrl = string.Empty;
        NewMonitorName = string.Empty;
        await SaveMonitorsAsync();
        RefreshCoordinatorState();
        RaiseMetricsChanged();
        AddLog($"已添加监控：{target.DisplayName}（{adapter.DisplayName}）。");
    }

    private async Task RemoveMonitorAsync(LiveMonitorTarget? target)
    {
        if (target is null)
            return;

        target.PropertyChanged -= Target_PropertyChanged;
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
        => await _coordinator.StopAsync();

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

    private void Coordinator_CheckResultChanged(LiveCheckResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var target = Monitors.FirstOrDefault(x => x.Id == result.TargetId);
            if (target is null)
                return;

            target.ApplyResult(result);
            CurrentTask = result.State == LiveMonitorState.Checking
                ? $"正在检查：{target.DisplayName}"
                : $"{target.DisplayName} · {target.StateText}";
            RaiseMetricsChanged();
        });
    }

    private void Coordinator_Log(string message)
        => Dispatcher.UIThread.Post(() => AddLog(message));

    private void Coordinator_RunningChanged(bool isRunning)
        => Dispatcher.UIThread.Post(() => IsMonitoring = isRunning);

    private void AttachTarget(LiveMonitorTarget target)
    {
        target.PropertyChanged += Target_PropertyChanged;
        Monitors.Add(target);
    }

    private void Target_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LiveMonitorTarget)
            return;

        if (e.PropertyName is nameof(LiveMonitorTarget.IsEnabled)
            or nameof(LiveMonitorTarget.DisplayName)
            or nameof(LiveMonitorTarget.ProfileUrl))
        {
            RefreshCoordinatorState();
            RaiseMetricsChanged();
            _ = SaveMonitorsAsync();
        }
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
    }

    private void RaiseMetricsChanged()
    {
        OnPropertyChanged(nameof(MonitorCount));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(LiveCount));
        OnPropertyChanged(nameof(NotDetectedCount));
        OnPropertyChanged(nameof(CheckingCount));
        OnPropertyChanged(nameof(FailedCount));
    }

    private void AddLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Logs.Add(line);
        while (Logs.Count > 500)
            Logs.RemoveAt(0);
    }

    public void AddRemoteLog(string message) => AddLog(message);

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
                ProfileUrl = item.ProfileUrl,
                IsEnabled = item.IsEnabled,
                StateText = item.StateText,
                StatusMessage = item.StatusMessage,
                LastCheckedText = item.LastCheckedText,
                StreamFormat = item.StreamFormat,
                StreamUrl = item.StreamUrl
            }).ToList(),
            Logs = Logs.TakeLast(120).ToList()
        };

    public Task StartRemoteMonitoringAsync() => StartMonitoringAsync();

    public Task StopRemoteMonitoringAsync() => StopMonitoringAsync();

    public Task CheckAllRemoteAsync() => CheckAllAsync();

    public async Task<string> AddRemoteMonitorAsync(string url, string? name)
    {
        var input = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
            return "请输入作者主页或直播分享地址。";

        var adapter = _platforms.ResolveByProfileUrl(input);
        if (adapter is null)
            return "暂不支持这个地址。当前版本已实现快手适配器。";

        var normalizedUrl = adapter.NormalizeProfileUrl(input);
        if (Monitors.Any(x => string.Equals(x.ProfileUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
            return "该地址已经在监控列表中。";

        var target = new LiveMonitorTarget
        {
            PlatformId = adapter.Id,
            ProfileUrl = normalizedUrl,
            DisplayName = string.IsNullOrWhiteSpace(name)
                ? BuildDefaultName(normalizedUrl, adapter.DisplayName)
                : name.Trim(),
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
        OnPropertyChanged(nameof(MonitorPanelButtonText));
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

    public async ValueTask DisposeAsync()
    {
        _coordinator.CheckResultChanged -= Coordinator_CheckResultChanged;
        _coordinator.Log -= Coordinator_Log;
        _coordinator.RunningChanged -= Coordinator_RunningChanged;

        foreach (var target in Monitors)
            target.PropertyChanged -= Target_PropertyChanged;

        await _coordinator.DisposeAsync();
        await _settingsService.SaveAsync(_settings);
        await SaveMonitorsAsync();
        _saveGate.Dispose();
    }
}
