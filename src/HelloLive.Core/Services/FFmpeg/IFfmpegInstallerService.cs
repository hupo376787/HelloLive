namespace HelloLive.Core.Services.FFmpeg;

public sealed record FfmpegInstallProgress(
    string Message,
    long BytesReceived = 0,
    long? TotalBytes = null,
    double BytesPerSecond = 0)
{
    public int? Percentage => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100L / TotalBytes.Value, 0, 100)
        : null;
}

public sealed record FfmpegInstallResult(
    string InstallDirectory,
    string FfmpegPath,
    string FfprobePath,
    string PackageUrl);

public sealed record FfmpegToolInfo(
    bool IsFound,
    string? FfmpegPath,
    string? FfprobePath);

public interface IFfmpegInstallerService
{
    bool IsSupported { get; }
    bool IsInstalled { get; }
    string InstallDirectory { get; }

    FfmpegToolInfo GetToolInfo();

    Task<FfmpegInstallResult> InstallAsync(
        IProgress<FfmpegInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
