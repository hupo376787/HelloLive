using HelloLive.Core.Models;

namespace HelloLive.Core.Services.Recording;

public sealed record PostRecordingCompressionOptions(
    bool Enabled,
    bool DeleteOriginal);

public sealed record LowDiskSpaceInfo(
    string StoragePath,
    long AvailableBytes,
    long ThresholdBytes);

public interface ILiveStreamRecorder : IAsyncDisposable
{
    string DownloadRoot { get; }

    event Action<string>? Log;
    event Action<LiveRecordingState>? RecordingStateChanged;
    event Action<string>? StorageChanged;
    event Action<LowDiskSpaceInfo>? LowDiskSpaceDetected;

    Task EnsureStorageAsync(CancellationToken cancellationToken = default);

    void SetPostRecordingCompressionOptions(
        PostRecordingCompressionOptions options);

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
