using HelloLive.Core.Models;
using HelloLive.Core.Services.Browser;
using HelloLive.Core.Sites;
using HelloLive.Core.Utilities;
using HelloLive.Desktop.Chromium;
using Microsoft.Playwright;
using System.Diagnostics;

namespace HelloLive.Desktop.Playwright;

/// <summary>
/// 整个应用只维护一个 Chromium 实例和一个 BrowserContext。
/// 每次检查临时创建 Page；捕获到 FLV/HLS 地址后立即阻止媒体下载并关闭 Page。
/// 并发上限由 LiveMonitorCoordinator 控制。
/// </summary>
public sealed class PlaywrightLiveBrowserService : ILiveBrowserService
{
    private readonly PlaywrightChromiumInstaller _installer;
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private bool _actualHeadless;
    private bool _disposed;

    public PlaywrightLiveBrowserService(PlaywrightChromiumInstaller installer)
    {
        _installer = installer;
    }

    public string PreferredChromiumInstallDirectory => _installer.PreferredInstallDirectory;

    public Task<int> InstallChromiumAsync(
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _installer.InstallAsync(progress, cancellationToken);

    public Task<string?> FindInstalledChromiumPathAsync(CancellationToken cancellationToken = default)
        => _installer.FindInstalledExecutablePathAsync(cancellationToken);

    public async Task<LiveCheckResult> CheckAsync(
        LiveMonitorTargetSnapshot target,
        ILivePlatformAdapter adapter,
        LiveMonitorOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options = options.Normalize();
        var context = await EnsureBrowserAsync(options.Headless, cancellationToken);
        var page = await context.NewPageAsync();
        var streamTcs = new TaskCompletionSource<LiveStreamInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalizedUrl = adapter.NormalizeProfileUrl(target.ProfileUrl);
        var stopwatch = Stopwatch.StartNew();
        string? resolvedPageUrl = null;
        string? authorIdFromApi = null;
        string? authorNameFromApi = null;
        string? avatarUrlFromApi = null;
        var authorIdentityTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await page.RouteAsync("**/*", async route =>
            {
                try
                {
                    var request = route.Request;
                    if (adapter.TryParseStreamRequest(request.Url, request.ResourceType, out var stream))
                    {
                        request.Headers.TryGetValue("referer", out var referer);
                        request.Headers.TryGetValue("origin", out var origin);
                        request.Headers.TryGetValue("user-agent", out var userAgent);
                        streamTcs.TrySetResult(stream with
                        {
                            RefererUrl = referer,
                            Origin = origin,
                            UserAgent = userAgent
                        });
                        await route.AbortAsync();
                        return;
                    }

                    if (options.BlockImagesAndFonts
                        && request.ResourceType is "image" or "font")
                    {
                        await route.AbortAsync();
                        return;
                    }

                    await route.ContinueAsync();
                }
                catch
                {
                    try { await route.ContinueAsync(); } catch { }
                }
            });

            page.Response += async (_, response) =>
            {
                try
                {
                    response.Headers.TryGetValue("content-type", out var contentType);
                    contentType ??= string.Empty;
                    if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                        && !contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (response.Headers.TryGetValue("content-length", out var lengthText)
                        && long.TryParse(lengthText, out var length)
                        && length > 2_000_000)
                    {
                        return;
                    }

                    var body = await response.TextAsync();

                    if (adapter.TryParseAuthorId(
                            response.Url,
                            contentType,
                            body,
                            out var parsedAuthorId)
                        && !string.IsNullOrWhiteSpace(parsedAuthorId))
                    {
                        authorIdFromApi = parsedAuthorId;
                    }

                    if (adapter.TryParseAuthorName(
                            response.Url,
                            contentType,
                            body,
                            out var parsedAuthorName)
                        && !string.IsNullOrWhiteSpace(parsedAuthorName))
                    {
                        authorNameFromApi = parsedAuthorName;
                    }

                    if (adapter.TryParseAuthorAvatar(
                            response.Url,
                            contentType,
                            body,
                            out var parsedAvatarUrl)
                        && !string.IsNullOrWhiteSpace(parsedAvatarUrl))
                    {
                        avatarUrlFromApi = parsedAvatarUrl;
                    }

                    if (!string.IsNullOrWhiteSpace(authorIdFromApi))
                        authorIdentityTcs.TrySetResult(true);

                    if (!streamTcs.Task.IsCompleted
                        && adapter.TryParseApiResponse(
                            response.Url,
                            contentType,
                            body,
                            out var stream))
                    {
                        streamTcs.TrySetResult(stream with
                        {
                            RefererUrl = page.Url,
                            AuthorName = authorNameFromApi
                        });
                    }
                }
                catch
                {
                    // 某些流式/压缩响应无法读取 body；真实媒体请求仍可被 Route 捕获。
                }
            };

            var navigationBudget = Math.Clamp(options.CheckTimeoutSeconds * 450, 3000, 12000);
            try
            {
                await page.GotoAsync(normalizedUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = navigationBudget
                });
            }
            catch (System.TimeoutException)
            {
                // 页面可能已经执行了足够多的 JS；继续等待网络中的直播地址。
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                // Playwright 超时同样继续等待网络中的直播地址。
            }

            resolvedPageUrl = page.Url;

            var remaining = TimeSpan.FromSeconds(options.CheckTimeoutSeconds) - stopwatch.Elapsed;
            if (remaining < TimeSpan.FromMilliseconds(300))
                remaining = TimeSpan.FromMilliseconds(300);

            var completed = await Task.WhenAny(streamTcs.Task, Task.Delay(remaining, cancellationToken));
            if (completed == streamTcs.Task)
            {
                var stream = await streamTcs.Task;

                // FLV request can appear slightly before Kuaishou's profile API response.
                // The media request is already aborted, so keeping the lightweight page
                // alive for a short grace window costs little and prevents using the
                // /u/{principalId} value as if it were author.id.
                if (string.IsNullOrWhiteSpace(authorIdFromApi))
                {
                    await Task.WhenAny(
                        authorIdentityTcs.Task,
                        Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken));
                }

                if (!string.IsNullOrWhiteSpace(authorNameFromApi))
                    stream = stream with { AuthorName = authorNameFromApi };

                var authorId = authorIdFromApi
                               ?? LiveAuthorIdentityHelper.ExtractStableAuthorId(
                                   target.ProfileUrl,
                                   page.Url);

                return LiveCheckResult.Live(
                    target.Id,
                    stream,
                    page.Url,
                    authorId,
                    authorNameFromApi,
                    avatarUrlFromApi);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return LiveCheckResult.NotDetected(
                target.Id,
                page.Url,
                authorIdFromApi
                ?? LiveAuthorIdentityHelper.ExtractStableAuthorId(target.ProfileUrl, page.Url));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LiveCheckResult.Error(target.Id, ex.Message, resolvedPageUrl ?? page.Url);
        }
        finally
        {
            try { await page.CloseAsync(); } catch { }
        }
    }

    private async Task<IBrowserContext> EnsureBrowserAsync(bool headless, CancellationToken cancellationToken)
    {
        await _browserGate.WaitAsync(cancellationToken);
        try
        {
            if (_context is not null && _browser is { IsConnected: true } && _actualHeadless == headless)
                return _context;

            await CloseBrowserCoreAsync();

            var executablePath = await _installer.FindInstalledExecutablePathAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException(
                    "未找到 Playwright Chromium。请先在 HelloLive 设置区域点击“安装 Chromium”。");
            }

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                ExecutablePath = executablePath,
                Args = ["--autoplay-policy=no-user-gesture-required"]
            });

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = false
            });
            _actualHeadless = headless;
            return _context;
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private async Task CloseBrowserCoreAsync()
    {
        if (_context is not null)
        {
            try { await _context.CloseAsync(); } catch { }
            _context = null;
        }

        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { }
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _browserGate.WaitAsync();
        try
        {
            await CloseBrowserCoreAsync();
        }
        finally
        {
            _browserGate.Release();
            _browserGate.Dispose();
        }
    }
}
