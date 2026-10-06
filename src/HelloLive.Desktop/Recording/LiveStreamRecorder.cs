using HelloLive.Core.Models;
using HelloLive.Core.Services.Recording;
using HelloLive.Core.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace HelloLive.Desktop.Recording;

/// <summary>
/// 实时直播录制器。
/// HTTP-FLV 直接按网络字节流持续写入 .flv；FLV 不依赖文件尾索引，
/// 即使进程异常退出，已经完整写入的前部标签仍可播放。
/// HLS 在检测到本机 FFmpeg 时写入 fragmented MP4，避免普通 MP4 因缺少尾部 moov 而损坏。
/// </summary>
public sealed class LiveStreamRecorder : ILiveStreamRecorder
{
    private sealed class RecordingSession
    {
        public required string TargetId { get; init; }
        public required string OutputPath { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required Task WorkerTask { get; set; }
    }

    private readonly ConcurrentDictionary<string, RecordingSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public LiveStreamRecorder()
    {
        DownloadRoot = ResolveDownloadRoot();
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string DownloadRoot { get; }

    public event Action<string>? Log;
    public event Action<LiveRecordingState>? RecordingStateChanged;

    public Task EnsureStorageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DownloadRoot);
        return Task.CompletedTask;
    }

    public Task StartIfNeededAsync(
        LiveMonitorTargetSnapshot target,
        LiveStreamInfo stream,
        string? resolvedPageUrl,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessions.ContainsKey(target.Id))
            return Task.CompletedTask;

        Directory.CreateDirectory(DownloadRoot);

        var authorId = string.IsNullOrWhiteSpace(target.AuthorId)
            ? LiveAuthorIdentityHelper.ExtractStableAuthorId(target.ProfileUrl, resolvedPageUrl)
            : target.AuthorId;

        if (string.IsNullOrWhiteSpace(authorId))
            authorId = target.Id;

        var authorName = !string.IsNullOrWhiteSpace(stream.AuthorName)
            ? stream.AuthorName.Trim()
            : string.IsNullOrWhiteSpace(target.DisplayName)
                ? authorId
                : target.DisplayName.Trim();

        var platformRoot = Path.Combine(
            DownloadRoot,
            PlatformFolderHelper.GetFolderName(target.PlatformId));
        Directory.CreateDirectory(platformRoot);

        var authorFolder = AuthorFolderResolver.Resolve(
            platformRoot,
            authorName,
            authorId);
        Directory.CreateDirectory(authorFolder);

        var startedAt = DateTimeOffset.Now;
        var extension = IsHttpFlv(stream)
            ? ".flv"
            : ".mp4";
        var outputPath = BuildUniqueOutputPath(authorFolder, startedAt, extension);

        var sessionCancellation = new CancellationTokenSource();
        var session = new RecordingSession
        {
            TargetId = target.Id,
            OutputPath = outputPath,
            Cancellation = sessionCancellation,
            WorkerTask = System.Threading.Tasks.Task.CompletedTask
        };

        if (!_sessions.TryAdd(target.Id, session))
        {
            sessionCancellation.Dispose();
            return Task.CompletedTask;
        }

        RecordingStateChanged?.Invoke(new LiveRecordingState(
            target.Id,
            true,
            outputPath,
            $"正在录制：{Path.GetFileName(outputPath)}",
            DateTimeOffset.Now));

        Log?.Invoke(
            $"{authorName}：开始实时录制 → {outputPath}");

        session.WorkerTask = Task.Run(
            () => RunSessionAsync(
                session,
                target,
                stream,
                resolvedPageUrl,
                sessionCancellation.Token),
            CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(
        string targetId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(targetId, out var session))
            return;

        session.Cancellation.Cancel();

        try
        {
            await session.WorkerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 具体错误已在 RunSessionAsync 中记录。
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values.ToArray();
        foreach (var session in sessions)
            session.Cancellation.Cancel();

        foreach (var session in sessions)
        {
            try
            {
                await session.WorkerTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }
    }

    private async Task RunSessionAsync(
        RecordingSession session,
        LiveMonitorTargetSnapshot target,
        LiveStreamInfo stream,
        string? resolvedPageUrl,
        CancellationToken cancellationToken)
    {
        string completionMessage;
        try
        {
            if (IsHttpFlv(stream))
            {
                await RecordHttpFlvAsync(
                    stream,
                    resolvedPageUrl ?? target.ProfileUrl,
                    session.OutputPath,
                    cancellationToken);
            }
            else if (IsHls(stream))
            {
                await RecordHlsWithFfmpegAsync(
                    stream,
                    resolvedPageUrl ?? target.ProfileUrl,
                    session.OutputPath,
                    cancellationToken);
            }
            else
            {
                throw new NotSupportedException(
                    $"当前录制器暂不支持 {stream.Format}：{stream.Url}");
            }

            completionMessage = cancellationToken.IsCancellationRequested
                ? $"录制已停止：{Path.GetFileName(session.OutputPath)}"
                : $"直播流已结束：{Path.GetFileName(session.OutputPath)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completionMessage = $"录制已停止：{Path.GetFileName(session.OutputPath)}";
        }
        catch (Exception ex)
        {
            completionMessage = $"录制异常结束：{ex.Message}";
            Log?.Invoke(
                $"{target.DisplayName}：录制失败 - {ex.Message}");
        }
        finally
        {
            if (_sessions.TryGetValue(session.TargetId, out var current)
                && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(session.TargetId, out _);
            }

            session.Cancellation.Dispose();
        }

        Log?.Invoke(
            $"{target.DisplayName}：{completionMessage}");

        RecordingStateChanged?.Invoke(new LiveRecordingState(
            target.Id,
            false,
            session.OutputPath,
            completionMessage,
            DateTimeOffset.Now));
    }

    private async Task RecordHttpFlvAsync(
        LiveStreamInfo stream,
        string fallbackReferer,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        ApplyRequestHeaders(request, stream, fallbackReferer);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("flv", StringComparison.OrdinalIgnoreCase)
            && !stream.Url.Contains(".flv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"直播响应不是 FLV：{contentType}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous
            | FileOptions.SequentialScan
            | FileOptions.WriteThrough);

        var buffer = new byte[256 * 1024];
        var lastFlush = Stopwatch.StartNew();

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            // 周期性落盘。FLV 本身是顺序 Tag 结构，不依赖文件尾 moov；
            // 即使程序崩溃，也只会丢失最后尚未完整写入的一小段。
            if (lastFlush.Elapsed >= TimeSpan.FromSeconds(2))
            {
                await output.FlushAsync(cancellationToken);
                lastFlush.Restart();
            }
        }

        await output.FlushAsync(cancellationToken);
    }

    private static async Task RecordHlsWithFfmpegAsync(
        LiveStreamInfo stream,
        string fallbackReferer,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = FindFfmpegExecutable();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new FileNotFoundException(
                "检测到 HLS 直播流，但未找到 FFmpeg。HTTP-FLV 可直接录制；HLS 请先安装 FFmpeg。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-rw_timeout");
        startInfo.ArgumentList.Add("15000000");

        var referer = stream.RefererUrl ?? fallbackReferer;
        if (!string.IsNullOrWhiteSpace(referer))
        {
            startInfo.ArgumentList.Add("-referer");
            startInfo.ArgumentList.Add(referer);
        }

        var userAgent = string.IsNullOrWhiteSpace(stream.UserAgent)
            ? DefaultUserAgent
            : stream.UserAgent;
        startInfo.ArgumentList.Add("-user_agent");
        startInfo.ArgumentList.Add(userAgent);

        if (!string.IsNullOrWhiteSpace(stream.Origin))
        {
            startInfo.ArgumentList.Add("-headers");
            startInfo.ArgumentList.Add($"Origin: {stream.Origin}\r\n");
        }

        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(stream.Url);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v?");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a?");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");

        // fragmented MP4 每个关键帧形成独立 fragment。
        // 不依赖直播结束时再写一个完整 moov，因此异常退出时仍可播放已落盘部分。
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+frag_keyframe+empty_moov+default_base_moof");
        startInfo.ArgumentList.Add("-flush_packets");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 FFmpeg。");

        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            var exitTask = process.WaitForExitAsync();
            var cancelledTask = Task.Delay(Timeout.Infinite, cancellationToken);
            var completed = await Task.WhenAny(exitTask, cancelledTask);

            if (completed == cancelledTask)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                }
                catch
                {
                }

                await Task.WhenAny(
                    process.WaitForExitAsync(),
                    Task.Delay(TimeSpan.FromSeconds(5)));

                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await stderrTask;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"FFmpeg 退出代码：{process.ExitCode}"
                        : error.Trim());
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        LiveStreamInfo stream,
        string fallbackReferer)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var referer = stream.RefererUrl ?? fallbackReferer;
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            request.Headers.Referrer = refererUri;

        var userAgent = string.IsNullOrWhiteSpace(stream.UserAgent)
            ? DefaultUserAgent
            : stream.UserAgent;
        request.Headers.UserAgent.ParseAdd(userAgent);

        var origin = stream.Origin;
        if (string.IsNullOrWhiteSpace(origin)
            && Uri.TryCreate(referer, UriKind.Absolute, out var originUri))
        {
            origin = originUri.GetLeftPart(UriPartial.Authority);
        }

        if (!string.IsNullOrWhiteSpace(origin))
            request.Headers.TryAddWithoutValidation("Origin", origin);
    }

    private static bool IsHttpFlv(LiveStreamInfo stream)
        => stream.Format.Equals("HTTP-FLV", StringComparison.OrdinalIgnoreCase)
           || stream.Url.Contains(".flv", StringComparison.OrdinalIgnoreCase);

    private static bool IsHls(LiveStreamInfo stream)
        => stream.Format.Equals("HLS", StringComparison.OrdinalIgnoreCase)
           || stream.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string BuildUniqueOutputPath(
        string authorFolder,
        DateTimeOffset startedAt,
        string extension)
    {
        var baseName = startedAt.ToLocalTime().ToString(
            "yyyy-MM-dd HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture);

        var candidate = Path.Combine(authorFolder, baseName + extension);
        if (!File.Exists(candidate))
            return candidate;

        for (var index = 1; index < 1000; index++)
        {
            candidate = Path.Combine(
                authorFolder,
                $"{baseName}_{index:00}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(
            authorFolder,
            $"{baseName}_{Guid.NewGuid():N}{extension}");
    }

    private static string ResolveDownloadRoot()
    {
        var preferred = Path.Combine(AppContext.BaseDirectory, "Download");
        try
        {
            Directory.CreateDirectory(preferred);
            return Path.GetFullPath(preferred);
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "HelloLive");
            Directory.CreateDirectory(fallback);
            return Path.GetFullPath(fallback);
        }
    }

    private static string? FindFfmpegExecutable()
    {
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, executable),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", executable),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", executable),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", executable)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
            }
        }

        return null;
    }

    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/141.0.0.0 Safari/537.36";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAllAsync();
        _httpClient.Dispose();
    }
}
