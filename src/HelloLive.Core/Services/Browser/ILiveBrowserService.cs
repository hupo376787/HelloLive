using HelloLive.Core.Models;
using HelloLive.Core.Sites;

namespace HelloLive.Core.Services.Browser;

public interface ILiveBrowserService : IAsyncDisposable
{
    string PreferredChromiumInstallDirectory { get; }

    Task<int> InstallChromiumAsync(
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string?> FindInstalledChromiumPathAsync(
        CancellationToken cancellationToken = default);

    Task<LiveCheckResult> CheckAsync(
        LiveMonitorTargetSnapshot target,
        ILivePlatformAdapter adapter,
        LiveMonitorOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record ChromiumInstallProgress(
    double? Percent,
    string Stage,
    string? Detail = null);
