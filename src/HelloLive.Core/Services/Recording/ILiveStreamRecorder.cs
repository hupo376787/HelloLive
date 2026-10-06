using HelloLive.Core.Models;

namespace HelloLive.Core.Services.Recording;

public interface ILiveStreamRecorder : IAsyncDisposable
{
    string DownloadRoot { get; }

    event Action<string>? Log;
    event Action<LiveRecordingState>? RecordingStateChanged;

    Task EnsureStorageAsync(CancellationToken cancellationToken = default);

    Task StartIfNeededAsync(
        LiveMonitorTargetSnapshot target,
        LiveStreamInfo stream,
        string? resolvedPageUrl,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        string targetId,
        CancellationToken cancellationToken = default);

    Task StopAllAsync(CancellationToken cancellationToken = default);
}
