using HelloLive.Core.Models;
using HelloLive.Core.Services.Browser;
using HelloLive.Core.Sites;
using System.Collections.Concurrent;

namespace HelloLive.Core.Services.Monitoring;

public sealed class LiveMonitorCoordinator : IAsyncDisposable
{
    private readonly ILiveBrowserService _browser;
    private readonly LivePlatformRegistry _platforms;
    private readonly HttpClient _httpClient = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private IReadOnlyList<LiveMonitorTargetSnapshot> _targets = Array.Empty<LiveMonitorTargetSnapshot>();
    private LiveMonitorOptions _options = new(true, 4, 60, 20, true);
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public LiveMonitorCoordinator(ILiveBrowserService browser, LivePlatformRegistry platforms)
    {
        _browser = browser;
        _platforms = platforms;
    }

    public bool IsRunning => _runTask is { IsCompleted: false };

    public event Action<LiveCheckResult>? CheckResultChanged;
    public event Action<string>? Log;
    public event Action<bool>? RunningChanged;

    public void SetTargets(IEnumerable<LiveMonitorTargetSnapshot> targets)
    {
        lock (_sync)
            _targets = targets.ToArray();
    }

    public void SetOptions(LiveMonitorOptions options)
    {
        lock (_sync)
            _options = options.Normalize();
    }

    public Task StartAsync()
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false })
                return Task.CompletedTask;

            _runCts = new CancellationTokenSource();
            _runTask = RunLoopAsync(_runCts.Token);
        }

        RunningChanged?.Invoke(true);
        return Task.CompletedTask;
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
        var (targets, options) = GetSnapshot();
        await CheckBatchAsync(targets.Where(x => x.IsEnabled).ToArray(), options, cancellationToken);
    }

    public async Task CheckOneAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var (targets, options) = GetSnapshot();
        var target = targets.FirstOrDefault(x => string.Equals(x.Id, targetId, StringComparison.Ordinal));
        if (target is null)
            return;

        await CheckTargetAsync(target, options, cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        Log?.Invoke("监控已启动。浏览器按需创建页面，检查完成后立即关闭。");

        while (!cancellationToken.IsCancellationRequested)
        {
            var (targets, options) = GetSnapshot();
            var enabled = targets.Where(x => x.IsEnabled).ToArray();
            if (enabled.Length > 0)
                await CheckBatchAsync(enabled, options, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(options.CheckIntervalSeconds), cancellationToken);
        }
    }

    private async Task CheckBatchAsync(
        IReadOnlyList<LiveMonitorTargetSnapshot> targets,
        LiveMonitorOptions options,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return;

        using var slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        var tasks = targets.Select(async target =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                await CheckTargetAsync(target, options, cancellationToken);
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

        CheckResultChanged?.Invoke(LiveCheckResult.Checking(target.Id));

        try
        {
            var adapter = !string.IsNullOrWhiteSpace(target.PlatformId)
                ? _platforms.GetRequired(target.PlatformId)
                : _platforms.ResolveByProfileUrl(target.ProfileUrl)
                  ?? throw new InvalidOperationException("无法识别该主页所属平台。");

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

            var name = string.IsNullOrWhiteSpace(target.DisplayName) ? target.ProfileUrl : target.DisplayName;
            Log?.Invoke(result.State == LiveMonitorState.Live
                ? $"{name}：发现 {result.Stream?.Format ?? "直播"} 流。"
                : $"{name}：{result.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CheckResultChanged?.Invoke(LiveCheckResult.Error(target.Id, ex.Message));
            Log?.Invoke($"{target.DisplayName}：检查失败 - {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(target.Id, out _);
        }
    }

    private (IReadOnlyList<LiveMonitorTargetSnapshot> Targets, LiveMonitorOptions Options) GetSnapshot()
    {
        lock (_sync)
            return (_targets, _options);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient.Dispose();
    }
}
