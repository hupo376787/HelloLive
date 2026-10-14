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
/// HTTP-FLV 按 Tag 实时落盘，并把上游直播时间戳归零到本地录像起点；
/// FLV 不依赖文件尾索引，即使进程异常退出，已完整落盘的前部 Tag 仍可播放。
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
    private readonly ConcurrentDictionary<string, Task> _compressionTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private const long LowDiskSpaceThresholdBytes = 1L * 1024 * 1024 * 1024;
    private static readonly TimeSpan DiskSpaceCheckInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _compressionGate = new(1, 1);
    private readonly CancellationTokenSource _compressionCancellation = new();
    private readonly CancellationTokenSource _diskSpaceCancellation = new();
    private readonly Task _diskSpaceMonitorTask;
    private readonly object _compressionOptionsSync = new();
    private PostRecordingCompressionOptions _compressionOptions = new(false, false);
    private readonly HttpClient _httpClient;
    private int _lowDiskAlertRaised;
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

        _diskSpaceMonitorTask = Task.Run(
            () => MonitorDiskSpaceAsync(_diskSpaceCancellation.Token));
    }

    public string DownloadRoot { get; }

    public event Action<string>? Log;
    public event Action<LiveRecordingState>? RecordingStateChanged;
    public event Action<string>? StorageChanged;
    public event Action<LowDiskSpaceInfo>? LowDiskSpaceDetected;

    public Task EnsureStorageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DownloadRoot);
        return Task.CompletedTask;
    }

    public void SetPostRecordingCompressionOptions(
        PostRecordingCompressionOptions options)
    {
        lock (_compressionOptionsSync)
            _compressionOptions = options;
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

        if (CheckAndHandleLowDiskSpace())
        {
            Log?.Invoke(
                $"{target.DisplayName}：磁盘剩余空间不超过 1 GB，已阻止启动录像。");
            return Task.CompletedTask;
        }

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
        StorageChanged?.Invoke(target.Id);

        QueuePostRecordingCompression(
            target.Id,
            target.DisplayName,
            session.OutputPath);
    }

    private void QueuePostRecordingCompression(
        string targetId,
        string displayName,
        string inputPath)
    {
        PostRecordingCompressionOptions options;
        lock (_compressionOptionsSync)
            options = _compressionOptions;

        if (!options.Enabled
            || _disposed
            || string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        if (CheckAndHandleLowDiskSpace())
        {
            Log?.Invoke(
                $"{displayName}：磁盘剩余空间不超过 1 GB，已跳过录像压缩，原文件保留。");
            return;
        }

        try
        {
            if (!File.Exists(inputPath)
                || new FileInfo(inputPath).Length <= 0)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        if (_compressionTasks.ContainsKey(inputPath))
            return;

        var task = Task.Run(
            () => CompressRecordingAsync(
                targetId,
                displayName,
                inputPath,
                options,
                _compressionCancellation.Token),
            CancellationToken.None);

        if (!_compressionTasks.TryAdd(inputPath, task))
            return;

        _ = task.ContinueWith(
            _ => _compressionTasks.TryRemove(inputPath, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CompressRecordingAsync(
        string targetId,
        string displayName,
        string inputPath,
        PostRecordingCompressionOptions options,
        CancellationToken cancellationToken)
    {
        await _compressionGate.WaitAsync(cancellationToken);
        string? temporaryPath = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ffmpegPath = FindFfmpegExecutable();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                Log?.Invoke(
                    $"{displayName}：录像压缩已跳过，未找到 FFmpeg。");
                return;
            }

            if (!File.Exists(inputPath))
                return;

            var originalInfo = new FileInfo(inputPath);
            if (originalInfo.Length <= 0)
                return;

            var finalPath = BuildCompressedOutputPath(inputPath);
            temporaryPath = finalPath
                            + ".hellolive-compress-"
                            + Guid.NewGuid().ToString("N")
                            + ".tmp.mp4";

            Log?.Invoke(
                $"{displayName}：开始后台压缩录像 → {Path.GetFileName(inputPath)}");

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
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:v:0?");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0?");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("libx264");
            startInfo.ArgumentList.Add("-preset");
            startInfo.ArgumentList.Add("medium");
            startInfo.ArgumentList.Add("-crf");
            startInfo.ArgumentList.Add("28");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("aac");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("96k");
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");
            startInfo.ArgumentList.Add(temporaryPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("无法启动 FFmpeg 压缩录像。");

            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                var exitTask = process.WaitForExitAsync();
                var cancelledTask = Task.Delay(
                    Timeout.Infinite,
                    cancellationToken);
                var completed = await Task.WhenAny(
                    exitTask,
                    cancelledTask);

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
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var error = await stderrTask;
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? $"FFmpeg 压缩退出代码：{process.ExitCode}"
                            : error.Trim());
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                }
            }

            if (!File.Exists(temporaryPath))
                throw new IOException("FFmpeg 未生成压缩文件。");

            var compressedBytes = new FileInfo(temporaryPath).Length;
            if (compressedBytes <= 0)
                throw new IOException("FFmpeg 生成的压缩文件为空。");

            var originalBytes = originalInfo.Length;
            if (compressedBytes >= originalBytes)
            {
                TryDeleteFile(temporaryPath);
                temporaryPath = null;

                Log?.Invoke(
                    $"{displayName}：压缩后未节省空间，已保留原录像，不生成压缩副本。");
                StorageChanged?.Invoke(targetId);
                return;
            }

            File.Move(temporaryPath, finalPath);
            temporaryPath = null;

            var savedPercent =
                100d * (originalBytes - compressedBytes) / originalBytes;

            if (options.DeleteOriginal)
            {
                try
                {
                    File.Delete(inputPath);
                    Log?.Invoke(
                        $"{displayName}：录像压缩完成，节省 {savedPercent:0.0}% 空间；原文件已删除 → {Path.GetFileName(finalPath)}");
                }
                catch (Exception ex)
                {
                    Log?.Invoke(
                        $"{displayName}：录像压缩完成，节省 {savedPercent:0.0}% 空间，但原文件删除失败，已同时保留两份 - {ex.Message}");
                }
            }
            else
            {
                Log?.Invoke(
                    $"{displayName}：录像压缩完成，节省 {savedPercent:0.0}% 空间；原文件已保留 → {Path.GetFileName(finalPath)}");
            }

            StorageChanged?.Invoke(targetId);
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down. The original recording remains untouched.
        }
        catch (Exception ex)
        {
            Log?.Invoke(
                $"{displayName}：录像压缩失败，原文件已保留 - {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
                TryDeleteFile(temporaryPath);

            _compressionGate.Release();
        }
    }

    private async Task MonitorDiskSpaceAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_sessions.Count > 0)
                    CheckAndHandleLowDiskSpace();

                await Task.Delay(
                    DiskSpaceCheckInterval,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool CheckAndHandleLowDiskSpace()
    {
        if (!TryGetAvailableFreeSpace(DownloadRoot, out var availableBytes))
            return false;

        if (availableBytes > LowDiskSpaceThresholdBytes)
        {
            Interlocked.Exchange(ref _lowDiskAlertRaised, 0);
            return false;
        }

        var info = new LowDiskSpaceInfo(
            DownloadRoot,
            availableBytes,
            LowDiskSpaceThresholdBytes);

        if (Interlocked.Exchange(ref _lowDiskAlertRaised, 1) == 0)
        {
            Log?.Invoke(
                $"磁盘空间不足：录像目录所在磁盘仅剩 {FormatFreeSpace(availableBytes)}，已达到 1 GB 停止阈值，正在停止所有录像。");
            LowDiskSpaceDetected?.Invoke(info);
        }

        foreach (var session in _sessions.Values.ToArray())
        {
            try
            {
                session.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return true;
    }

    private static bool TryGetAvailableFreeSpace(
        string path,
        out long availableBytes)
    {
        availableBytes = 0;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;

            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatFreeSpace(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{Math.Max(0, bytes) / 1024d:0.0} KB";
    }

    private static string BuildCompressedOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath)
                        ?? AppContext.BaseDirectory;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var candidate = Path.Combine(
            directory,
            name + "_compressed.mp4");

        if (!File.Exists(candidate))
            return candidate;

        for (var index = 2; index < 10000; index++)
        {
            candidate = Path.Combine(
                directory,
                $"{name}_compressed_{index}.mp4");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(
            directory,
            $"{name}_compressed_{Guid.NewGuid():N}.mp4");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
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

        // 不再把 HTTP-FLV 原始字节直接复制到文件：
        // 快手直播流的 Tag 时间戳可能从主播开播时刻开始。例如本地只录 28 分钟，
        // 上游首个 Tag 已经是 2 小时左右，播放器就会错误显示 2:29:48。
        // FlvStreamWriter 会把首个音/视频 Tag 归零，并且只写入完整 Tag。
        await FlvStreamWriter.CopyNormalizedAsync(
            input,
            output,
            cancellationToken);
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

        _diskSpaceCancellation.Cancel();
        try
        {
            await _diskSpaceMonitorTask;
        }
        catch (OperationCanceledException)
        {
        }

        _compressionCancellation.Cancel();

        var compressionTasks = _compressionTasks.Values.ToArray();
        if (compressionTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(compressionTasks);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        _diskSpaceCancellation.Dispose();
        _compressionCancellation.Dispose();
        _compressionGate.Dispose();
        _httpClient.Dispose();
    }
}
