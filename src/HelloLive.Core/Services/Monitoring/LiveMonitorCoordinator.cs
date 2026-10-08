using HelloLive.Core.Models;
using HelloLive.Core.Services.Browser;
using HelloLive.Core.Services.Recording;
using HelloLive.Core.Sites;
using System.Collections.Concurrent;

namespace HelloLive.Core.Services.Monitoring;

public sealed class LiveMonitorCoordinator : IAsyncDisposable
{
    private readonly ILiveBrowserService _browser;
    private readonly LivePlatformRegistry _platforms;
    private readonly ILiveStreamRecorder _recorder;
    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _browserModeGate = new(1, 1);
    private readonly object _sync = new();
    private IReadOnlyList<LiveMonitorTargetSnapshot> _targets = Array.Empty<LiveMonitorTargetSnapshot>();
    private LiveMonitorOptions _options = new(true, 4, 300, 20, true);
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public LiveMonitorCoordinator(
        ILiveBrowserService browser,
        LivePlatformRegistry platforms,
        ILiveStreamRecorder recorder)
    {
        _browser = browser;
        _platforms = platforms;
        _recorder = recorder;
        _recorder.Log += Recorder_Log;
        _recorder.RecordingStateChanged += Recorder_RecordingStateChanged;
    }

    public bool IsRunning => _runTask is { IsCompleted: false };
    public string DownloadRoot => _recorder.DownloadRoot;

    public event Action<LiveCheckResult>? CheckResultChanged;
    public event Action<LiveRecordingState>? RecordingStateChanged;
    public event Action<string>? Log;
    public event Action<bool>? RunningChanged;

    public void SetTargets(IEnumerable<LiveMonitorTargetSnapshot> targets)
    {
        lock (_sync)
            _targets = targets.ToArray();
    }

    public void SetOptions(LiveMonitorOptions options)
    {
        // Headless now controls only user-triggered checks. Background polling below
        // explicitly overrides it to true so scheduled monitoring never opens Chromium.
        var normalized = options.Normalize();

        lock (_sync)
            _options = normalized;
    }

    public async Task StartAsync()
    {
        await _recorder.EnsureStorageAsync();

        lock (_sync)
        {
            if (_runTask is { IsCompleted: false })
                return;

            _runCts = new CancellationTokenSource();
            _runTask = RunLoopAsync(_runCts.Token);
        }

        Log?.Invoke($"监控已启动。直播录像目录：{DownloadRoot}");
        RunningChanged?.Invoke(true);
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (_sync)
        {
            _runCts?.Cancel();
            task = _runTask;
        }

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // “停止监控”同时停止当前直播录制。HTTP-FLV 文件可直接保留，
        // HLS 使用的 fragmented MP4 会先尝试让 FFmpeg 正常收尾。
        await _recorder.StopAllAsync();

        lock (_sync)
        {
            _runCts?.Dispose();
            _runCts = null;
            _runTask = null;
        }

        RunningChanged?.Invoke(false);
    }

    public async Task CheckAllOnceAsync(CancellationToken cancellationToken = default)
    {
        await _browserModeGate.WaitAsync(cancellationToken);
        try
        {
            var (targets, options) = GetSnapshot();
            await CheckBatchAsync(
                targets.Where(x => x.IsEnabled).ToArray(),
                options,
                cancellationToken);
        }
        finally
        {
            _browserModeGate.Release();
        }
    }

    public async Task CheckOneAsync(
        string targetId,
        CancellationToken cancellationToken = default)
    {
        await _browserModeGate.WaitAsync(cancellationToken);
        try
        {
            var (targets, options) = GetSnapshot();
            var target = targets.FirstOrDefault(
                x => string.Equals(x.Id, targetId, StringComparison.Ordinal));
            if (target is null)
                return;

            // A single monitor check is used by the monitor-card context menu,
            // newly-added monitor probing, URL-change probing and remote single-item checks.
            // These must never surface Chromium, regardless of the manual check-all switch.
            await CheckTargetAsync(
                target,
                options with { Headless = true },
                cancellationToken);
        }
        finally
        {
            _browserModeGate.Release();
        }
    }

    public Task StopRecordingAsync(
        string targetId,
        CancellationToken cancellationToken = default)
        => _recorder.StopAsync(targetId, cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        Log?.Invoke("后台轮询和监控项“立即检查”固定使用无头 Chromium；“立即检查全部”是否显示 Chromium 由左侧开关决定。检测到直播后关闭临时 Page，并由独立录制连接持续保存。");

        while (!cancellationToken.IsCancellationRequested)
        {
            var (targets, options) = GetSnapshot();
            var enabled = targets.Where(x => x.IsEnabled).ToArray();
            if (enabled.Length > 0)
            {
                await _browserModeGate.WaitAsync(cancellationToken);
                try
                {
                    var backgroundOptions = options with { Headless = true };
                    await CheckBatchAsync(
                        enabled,
                        backgroundOptions,
                        cancellationToken);
                }
                finally
                {
                    _browserModeGate.Release();
                }
            }

            await Task.Delay(
                TimeSpan.FromSeconds(options.CheckIntervalSeconds),
                cancellationToken);
        }
    }

    private async Task CheckBatchAsync(
        IReadOnlyList<LiveMonitorTargetSnapshot> targets,
        LiveMonitorOptions options,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return;

        using var slots = new SemaphoreSlim(
            options.MaxConcurrency,
            options.MaxConcurrency);

        var tasks = targets.Select(async target =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                await CheckTargetAsync(
                    target,
                    options,
                    cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task CheckTargetAsync(
        LiveMonitorTargetSnapshot target,
        LiveMonitorOptions options,
        CancellationToken cancellationToken)
    {
        if (!_inFlight.TryAdd(target.Id, 0))
            return;

        CheckResultChanged?.Invoke(
            LiveCheckResult.Checking(target.Id));

        try
        {
            var adapter = !string.IsNullOrWhiteSpace(target.PlatformId)
                ? _platforms.GetRequired(target.PlatformId)
                : _platforms.ResolveByProfileUrl(target.ProfileUrl)
                  ?? throw new InvalidOperationException(
                      "无法识别该主页所属平台。");

            var direct = await adapter.TryCheckDirectAsync(
                _httpClient,
                target,
                options,
                cancellationToken);

            var result = direct ?? await _browser.CheckAsync(
                target,
                adapter,
                options,
                cancellationToken);

            CheckResultChanged?.Invoke(result);

            if (IsRunning
                && result.State == LiveMonitorState.Live
                && result.Stream is not null)
            {
                var recordingTarget = string.IsNullOrWhiteSpace(result.AuthorId)
                    ? target
                    : target with { AuthorId = result.AuthorId };

                await _recorder.StartIfNeededAsync(
                    recordingTarget,
                    result.Stream,
                    result.ResolvedPageUrl,
                    cancellationToken);
            }

            var name = string.IsNullOrWhiteSpace(target.DisplayName)
                ? target.ProfileUrl
                : target.DisplayName;
            Log?.Invoke(result.State == LiveMonitorState.Live
                ? $"{name}：发现 {result.Stream?.Format ?? "直播"} 流。"
                : $"{name}：{result.Message}");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CheckResultChanged?.Invoke(
                LiveCheckResult.Error(target.Id, ex.Message));
            Log?.Invoke(
                $"{target.DisplayName}：检查失败 - {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(target.Id, out _);
        }
    }

    private (
        IReadOnlyList<LiveMonitorTargetSnapshot> Targets,
        LiveMonitorOptions Options) GetSnapshot()
    {
        lock (_sync)
            return (_targets, _options);
    }

    private void Recorder_Log(string message)
        => Log?.Invoke(message);

    private void Recorder_RecordingStateChanged(LiveRecordingState state)
        => RecordingStateChanged?.Invoke(state);

    public async ValueTask DisposeAsync()
    {
        _recorder.Log -= Recorder_Log;
        _recorder.RecordingStateChanged -= Recorder_RecordingStateChanged;
        await StopAsync();
        await _recorder.DisposeAsync();
        _httpClient.Dispose();
        _browserModeGate.Dispose();
    }
}
