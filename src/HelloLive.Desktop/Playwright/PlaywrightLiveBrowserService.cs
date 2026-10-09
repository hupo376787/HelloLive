using HelloLive.Core.Models;
using HelloLive.Core.Services.Browser;
using HelloLive.Core.Sites;
using HelloLive.Core.Utilities;
using HelloLive.Desktop.Chromium;
using Microsoft.Playwright;
using System.Diagnostics;
using System.Text.Json;

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
                        // Do not snapshot the current fallback nickname into the
                        // stream object. Kuaishou may return the authoritative
                        // /live_api/baseuser/userinfo/byid response slightly later.
                        // Keeping AuthorName owned by the platform stream parser lets the
                        // final result use the newest author metadata instead of a stale
                        // recommendation/DOM name captured when the stream first appeared.
                        streamTcs.TrySetResult(stream with
                        {
                            RefererUrl = page.Url
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

            // Even when the author is offline, Kuaishou usually exposes the profile
            // nickname in the page title / OG metadata and the avatar URL in the DOM.
            // Capture that immediately, then refresh it once more before returning.
            await MergeAuthorMetadataFromDomAsync(
                page,
                name => authorNameFromApi ??= name,
                avatar => avatarUrlFromApi ??= avatar);

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
                        Task.Delay(
                            TimeSpan.FromMilliseconds(
                                adapter.Id.Equals("kuaishou", StringComparison.OrdinalIgnoreCase)
                                    ? 1500
                                    : 900),
                            cancellationToken));
                }

                await MergeAuthorMetadataFromDomAsync(
                    page,
                    name => authorNameFromApi ??= name,
                    avatar => avatarUrlFromApi ??= avatar);

                // The stream response is the strongest evidence for the live author's
                // nickname. Kuaishou may continue loading recommendation GraphQL after
                // the FLV URL is found; those responses can contain other creators.
                // Never let a later recommendation overwrite the name captured with
                // the actual live stream.
                var resolvedAuthorName = !string.IsNullOrWhiteSpace(stream.AuthorName)
                    ? stream.AuthorName
                    : authorNameFromApi;

                if (string.IsNullOrWhiteSpace(stream.AuthorName)
                    && !string.IsNullOrWhiteSpace(resolvedAuthorName))
                {
                    stream = stream with { AuthorName = resolvedAuthorName };
                }

                var authorId = authorIdFromApi
                               ?? LiveAuthorIdentityHelper.ExtractStableAuthorId(
                                   target.ProfileUrl,
                                   page.Url);

                return LiveCheckResult.Live(
                    target.Id,
                    stream,
                    page.Url,
                    authorId,
                    resolvedAuthorName,
                    avatarUrlFromApi);
            }

            cancellationToken.ThrowIfCancellationRequested();

            await MergeAuthorMetadataFromDomAsync(
                page,
                name => authorNameFromApi ??= name,
                avatar => avatarUrlFromApi ??= avatar);

            return LiveCheckResult.NotDetected(
                target.Id,
                page.Url,
                authorIdFromApi
                ?? LiveAuthorIdentityHelper.ExtractStableAuthorId(target.ProfileUrl, page.Url),
                authorNameFromApi,
                avatarUrlFromApi);
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

    private static async Task MergeAuthorMetadataFromDomAsync(
        IPage page,
        Action<string> setName,
        Action<string> setAvatar)
    {
        try
        {
            var result = await page.EvaluateAsync<JsonElement>("""
                () => {
                    const firstAttr = (selectors, attrs) => {
                        for (const selector of selectors) {
                            const element = document.querySelector(selector);
                            if (!element) continue;

                            for (const attr of attrs) {
                                const value = element.getAttribute?.(attr);
                                if (value && /^https?:\/\//i.test(value.trim()))
                                    return value.trim();
                            }

                            if (element instanceof HTMLImageElement) {
                                const value = element.currentSrc || element.src;
                                if (value && /^https?:\/\//i.test(value.trim()))
                                    return value.trim();
                            }
                        }
                        return "";
                    };

                    const normalizeName = value => {
                        if (!value) return "";
                        let text = String(value).replace(/\s+/g, " ").trim();
                        text = text.replace(/\s*[-_|·]\s*(快手直播|快手|Kuaishou).*$/i, "").trim();
                        if (/^(快手直播|快手|Kuaishou)$/i.test(text)) return "";
                        return text;
                    };

                    let name = "";
                    for (const selector of [
                        '[class*="profile" i] [class*="name" i]',
                        '[class*="user" i] [class*="name" i]',
                        '[class*="nickname" i]',
                        '[class*="user-name" i]',
                        '[class*="profile-name" i]',
                        'meta[property="og:title"]',
                        'meta[name="twitter:title"]'
                    ]) {
                        const element = document.querySelector(selector);
                        const candidate = element?.getAttribute?.("content") || element?.textContent || "";
                        name = normalizeName(candidate);
                        if (name) break;
                    }

                    if (!name)
                        name = normalizeName(document.title);

                    const avatar = firstAttr(
                        [
                            '[class*="profile" i] [class*="avatar" i] img',
                            '[class*="user" i] [class*="avatar" i] img',
                            '[class*="avatar" i] img',
                            'img[class*="avatar" i]',
                            '[class*="head" i] img',
                            'meta[property="og:image"]',
                            'meta[name="twitter:image"]',
                            'link[rel="image_src"]'
                        ],
                        ['content', 'href', 'src', 'data-src', 'data-original']
                    );

                    return { name, avatar };
                }
                """);

            if (result.ValueKind != JsonValueKind.Object)
                return;

            if (result.TryGetProperty("name", out var nameElement))
            {
                var name = nameElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    setName(name);
            }

            if (result.TryGetProperty("avatar", out var avatarElement))
            {
                var avatar = avatarElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(avatar))
                    setAvatar(avatar);
            }
        }
        catch
        {
            // DOM metadata is only a fallback; network-response parsing remains primary.
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
