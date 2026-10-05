using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelloLive.Core.Contracts;
using HelloLive.Core.Remote.Services;
using System.Collections.ObjectModel;

namespace HelloLive.Core.Remote.ViewModels;

public sealed class RemoteMainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RemoteLiveClient _client;
    private readonly IRemoteClientPreferencesStore _preferencesStore;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private CancellationTokenSource? _pollingCts;

    private string _serverAddress = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? string.Empty
        : "http://127.0.0.1:5088";
    private string _accessToken = string.Empty;
    private bool _isConnected;
    private bool _isConnecting;
    private string _connectionText = "尚未连接";
    private bool _isMonitoring;
    private string _currentTask = "-";
    private int _monitorCount;
    private int _enabledCount;
    private int _liveCount;
    private int _notDetectedCount;
    private int _checkingCount;
    private int _failedCount;
    private string _newMonitorUrl = string.Empty;
    private string _newMonitorName = string.Empty;
    private bool _isDarkTheme = true;

    public RemoteMainViewModel(RemoteLiveClient client)
    {
        _client = client;
        _preferencesStore = RemoteClientPreferencesStoreProvider.Current;

        var preferences = _preferencesStore.Load();
        if (!string.IsNullOrWhiteSpace(preferences.ServerAddress))
            _serverAddress = preferences.ServerAddress;
        _accessToken = preferences.AccessToken;
        _isDarkTheme = preferences.IsDarkTheme;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnecting);
        StartMonitoringCommand = new AsyncRelayCommand(
            () => ExecuteHostActionAsync("start"),
            () => IsConnected);
        StopMonitoringCommand = new AsyncRelayCommand(
            () => ExecuteHostActionAsync("stop"),
            () => IsConnected);
        CheckAllCommand = new AsyncRelayCommand(
            () => ExecuteHostActionAsync("check-all"),
            () => IsConnected);
        AddMonitorCommand = new AsyncRelayCommand(AddMonitorAsync, () => IsConnected);
        RefreshCommand = new AsyncRelayCommand(RefreshSnapshotAsync, () => IsConnected);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        ApplyTheme();
    }

    public ObservableCollection<RemoteMonitorDto> Monitors { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand StartMonitoringCommand { get; }
    public IAsyncRelayCommand StopMonitoringCommand { get; }
    public IAsyncRelayCommand CheckAllCommand { get; }
    public IAsyncRelayCommand AddMonitorCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }

    public string ServerAddress
    {
        get => _serverAddress;
        set => SetProperty(ref _serverAddress, value ?? string.Empty);
    }

    public string AccessToken
    {
        get => _accessToken;
        set => SetProperty(ref _accessToken, value ?? string.Empty);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                RefreshCommands();
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
                RefreshCommands();
        }
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetProperty(ref _connectionText, value);
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

    public string MonitoringStatusText => IsMonitoring ? "桌面端监控运行中" : "桌面端监控已停止";

    public string CurrentTask
    {
        get => _currentTask;
        private set => SetProperty(ref _currentTask, value);
    }

    public int MonitorCount { get => _monitorCount; private set => SetProperty(ref _monitorCount, value); }
    public int EnabledCount { get => _enabledCount; private set => SetProperty(ref _enabledCount, value); }
    public int LiveCount { get => _liveCount; private set => SetProperty(ref _liveCount, value); }
    public int NotDetectedCount { get => _notDetectedCount; private set => SetProperty(ref _notDetectedCount, value); }
    public int CheckingCount { get => _checkingCount; private set => SetProperty(ref _checkingCount, value); }
    public int FailedCount { get => _failedCount; private set => SetProperty(ref _failedCount, value); }

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

    public string ThemeIcon => _isDarkTheme ? "☀" : "☾";

    public string ConnectionHint => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? "手机端请填写桌面端显示的局域网地址，不能使用 127.0.0.1 或 localhost。"
        : "同一台电脑可使用 127.0.0.1；其他设备请填写桌面端显示的局域网地址。";

    private async Task ConnectAsync()
    {
        if (IsConnecting)
            return;

        IsConnecting = true;
        ConnectionText = "正在连接…";
        StopPolling();

        try
        {
            _client.Configure(ServerAddress, AccessToken);
            var health = await _client.GetHealthAsync();
            if (!string.Equals(health.Service, "HelloLive", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"目标服务不是 HelloLive：{health.Service}");

            IsConnected = true;
            ConnectionText = $"已连接 · {health.Service}";
            SavePreferences();
            AddLog("已连接桌面端 HelloLive。");

            await RefreshSnapshotAsync();
            StartPolling();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionText = $"连接失败：{ex.Message}";
            AddLog(ConnectionText);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task AddMonitorAsync()
    {
        var url = NewMonitorUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            AddLog("请输入作者主页或直播分享地址。");
            return;
        }

        var result = await _client.AddMonitorAsync(new RemoteAddMonitorRequest
        {
            Url = url,
            Name = NewMonitorName.Trim()
        });
        AddLog(result.Message);

        if (result.Success)
        {
            NewMonitorUrl = string.Empty;
            NewMonitorName = string.Empty;
            await RefreshSnapshotAsync();
        }
    }

    public async Task CheckMonitorAsync(RemoteMonitorDto item)
    {
        var result = await _client.CheckMonitorAsync(item.Id);
        AddLog(result.Message);
        if (result.Success)
            await RefreshSnapshotAsync();
    }

    public async Task RemoveMonitorAsync(RemoteMonitorDto item)
    {
        var result = await _client.RemoveMonitorAsync(item.Id);
        AddLog(result.Message);
        if (result.Success)
            await RefreshSnapshotAsync();
    }

    public async Task ToggleMonitorEnabledAsync(RemoteMonitorDto item)
    {
        var result = await _client.SetMonitorEnabledAsync(item.Id, !item.IsEnabled);
        AddLog(result.Message);
        if (result.Success)
            await RefreshSnapshotAsync();
    }

    private async Task ExecuteHostActionAsync(string action)
    {
        try
        {
            var result = await _client.ExecuteActionAsync(action);
            AddLog(result.Message);
            await RefreshSnapshotAsync();
        }
        catch (Exception ex)
        {
            AddLog($"远程命令失败：{ex.Message}");
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        if (!IsConnected)
            return;

        if (!await _snapshotGate.WaitAsync(0))
            return;

        try
        {
            var snapshot = await _client.GetSnapshotAsync();
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            ConnectionText = $"连接异常：{ex.Message}";
            AddLog(ConnectionText);
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private void ApplySnapshot(RemoteLiveSnapshot snapshot)
    {
        IsMonitoring = snapshot.IsMonitoring;
        CurrentTask = string.IsNullOrWhiteSpace(snapshot.CurrentTask) ? "-" : snapshot.CurrentTask;
        MonitorCount = snapshot.MonitorCount;
        EnabledCount = snapshot.EnabledCount;
        LiveCount = snapshot.LiveCount;
        NotDetectedCount = snapshot.NotDetectedCount;
        CheckingCount = snapshot.CheckingCount;
        FailedCount = snapshot.FailedCount;

        Monitors.Clear();
        foreach (var monitor in snapshot.Monitors)
            Monitors.Add(monitor);

        Logs.Clear();
        foreach (var line in snapshot.Logs.TakeLast(120))
            Logs.Add(line);

        ConnectionText = $"已连接 · 更新于 {snapshot.ServerTime.ToLocalTime():HH:mm:ss}";
    }

    private void StartPolling()
    {
        StopPolling();
        _pollingCts = new CancellationTokenSource();
        _ = PollLoopAsync(_pollingCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(RefreshSnapshotAsync);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Polling errors are surfaced by RefreshSnapshotAsync.
            }
        }
    }

    private void StopPolling()
    {
        var cts = Interlocked.Exchange(ref _pollingCts, null);
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme();
        SavePreferences();
        OnPropertyChanged(nameof(ThemeIcon));
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void SavePreferences()
    {
        _preferencesStore.Save(new RemoteClientPreferences
        {
            ServerAddress = ServerAddress.Trim(),
            AccessToken = AccessToken.Trim(),
            IsDarkTheme = _isDarkTheme
        });
    }

    private void AddLog(string message)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Logs.Count > 200)
            Logs.RemoveAt(0);
    }

    private void RefreshCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        StartMonitoringCommand.NotifyCanExecuteChanged();
        StopMonitoringCommand.NotifyCanExecuteChanged();
        CheckAllCommand.NotifyCanExecuteChanged();
        AddMonitorCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    public ValueTask DisposeAsync()
    {
        StopPolling();
        _client.Dispose();
        _snapshotGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
