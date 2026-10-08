using HelloLive.Core.Services.FFmpeg;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HelloLive.Desktop.FFmpeg;

/// <summary>
/// Windows x64 FFmpeg installer, modeled after HelloCrab:
/// download the latest gyan.dev release essentials ZIP in the background,
/// then install ffmpeg.exe/ffprobe.exe under AppContext.BaseDirectory/ffmpeg/bin.
/// </summary>
public sealed class GyanFfmpegInstallerService : IFfmpegInstallerService, IDisposable
{
    private const string BuildsPageUrl = "https://www.gyan.dev/ffmpeg/builds/";
    private const string FallbackPackageUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;

    private static readonly Regex PackageLinkRegex = new(
        "href\\s*=\\s*[\\\"'](?<href>[^\\\"']*ffmpeg-release-essentials\\.zip(?:\\?[^\\\"']*)?)[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public GyanFfmpegInstallerService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "HelloLive/1.0 (+https://www.gyan.dev/ffmpeg/builds/)");
    }

    public bool IsSupported => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem;

    public string InstallDirectory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public bool IsInstalled => FindExistingPair() is not null;

    public FfmpegToolInfo GetToolInfo()
    {
        var pair = FindExistingPair();
        return pair is null
            ? new FfmpegToolInfo(false, null, null)
            : new FfmpegToolInfo(
                true,
                Path.GetFullPath(pair.Value.Ffmpeg),
                Path.GetFullPath(pair.Value.Ffprobe));
    }

    public async Task<FfmpegInstallResult> InstallAsync(
        IProgress<FfmpegInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("FFmpeg 自动下载当前仅支持 Windows。");
        if (!Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("gyan.dev 的 Windows 构建为 64 位，当前系统无法自动安装。");

        var packageUri = await ResolvePackageUriAsync(cancellationToken);
        progress?.Report(new FfmpegInstallProgress(
            $"已找到 FFmpeg release essentials：{packageUri.AbsolutePath.Split('/').Last()}"));

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "HelloLive",
            "ffmpeg-install-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempRoot, "ffmpeg-release-essentials.zip");
        var extractPath = Path.Combine(tempRoot, "extract");

        Directory.CreateDirectory(tempRoot);

        try
        {
            await DownloadPackageAsync(packageUri, archivePath, progress, cancellationToken);

            progress?.Report(new FfmpegInstallProgress("下载完成，正在校验并解压 FFmpeg…"));
            Directory.CreateDirectory(extractPath);
            await ExtractArchiveAsync(archivePath, extractPath, cancellationToken);

            var sourceBin = FindSourceBinDirectory(extractPath);
            var sourceFfmpeg = Path.Combine(sourceBin, "ffmpeg.exe");
            var sourceFfprobe = Path.Combine(sourceBin, "ffprobe.exe");

            if (!IsUsableFile(sourceFfmpeg) || !IsUsableFile(sourceFfprobe))
                throw new InvalidDataException("压缩包中没有找到有效的 ffmpeg.exe 和 ffprobe.exe。");

            var destinationBin = Path.Combine(InstallDirectory, "bin");
            Directory.CreateDirectory(destinationBin);

            progress?.Report(new FfmpegInstallProgress(
                $"正在安装到：{destinationBin}"));

            var destinationFfmpeg = Path.Combine(destinationBin, "ffmpeg.exe");
            var destinationFfprobe = Path.Combine(destinationBin, "ffprobe.exe");

            CopyFileAtomically(sourceFfmpeg, destinationFfmpeg);
            CopyFileAtomically(sourceFfprobe, destinationFfprobe);

            var sourceFfplay = Path.Combine(sourceBin, "ffplay.exe");
            if (IsUsableFile(sourceFfplay))
                CopyFileAtomically(sourceFfplay, Path.Combine(destinationBin, "ffplay.exe"));

            if (!IsInstalled)
                throw new IOException("FFmpeg 文件复制完成，但安装结果校验失败。");

            progress?.Report(new FfmpegInstallProgress("FFmpeg 安装完成。"));

            return new FfmpegInstallResult(
                InstallDirectory,
                destinationFfmpeg,
                destinationFfprobe,
                packageUri.AbsoluteUri);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"无法写入程序目录“{InstallDirectory}”。请确认 HelloLive 所在目录可写。",
                ex);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<Uri> ResolvePackageUriAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BuildsPageUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = PackageLinkRegex.Match(html);

            if (match.Success)
            {
                var href = WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());
                var uri = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                    ? absolute
                    : new Uri(new Uri(BuildsPageUrl), href);

                ValidatePackageUri(uri);
                return uri;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Fall back to gyan.dev stable latest package.
        }

        var fallback = new Uri(FallbackPackageUrl);
        ValidatePackageUri(fallback);
        return fallback;
    }

    private async Task DownloadPackageAsync(
        Uri packageUri,
        string archivePath,
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        request.Headers.Referrer = new Uri(BuildsPageUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes is > MaximumPackageBytes)
            throw new InvalidDataException("FFmpeg 压缩包大小异常，已取消下载。");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[256 * 1024];
        long received = 0;
        var timer = Stopwatch.StartNew();
        var lastReport = DateTimeOffset.MinValue;

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            received += read;
            if (received > MaximumPackageBytes)
                throw new InvalidDataException("FFmpeg 压缩包超过允许的最大大小。");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            var now = DateTimeOffset.UtcNow;
            if (now - lastReport >= TimeSpan.FromMilliseconds(350))
            {
                lastReport = now;
                progress?.Report(new FfmpegInstallProgress(
                    "正在下载 FFmpeg",
                    received,
                    totalBytes,
                    CalculateBytesPerSecond(received, timer.Elapsed)));
            }
        }

        await output.FlushAsync(cancellationToken);

        if (received == 0)
            throw new InvalidDataException("FFmpeg 下载结果为空。");

        progress?.Report(new FfmpegInstallProgress(
            "正在下载 FFmpeg",
            received,
            totalBytes,
            CalculateBytesPerSecond(received, timer.Elapsed)));
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string extractPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var root = Path.GetFullPath(extractPath) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var target = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FFmpeg 压缩包包含非法路径。");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var source = entry.Open();
            await using var destination = new FileStream(
                target,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await source.CopyToAsync(destination, 256 * 1024, cancellationToken);
        }
    }

    private static string FindSourceBinDirectory(string extractPath)
    {
        var ffmpegPath = Directory.EnumerateFiles(
                extractPath,
                "ffmpeg.exe",
                SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase));

        return ffmpegPath is null
            ? throw new InvalidDataException("解压后没有找到 FFmpeg bin 目录。")
            : Path.GetDirectoryName(ffmpegPath)!;
    }

    private (string Ffmpeg, string Ffprobe)? FindExistingPair()
    {
        var candidates = new[]
        {
            Path.Combine(InstallDirectory, "bin"),
            InstallDirectory,
            AppContext.BaseDirectory
        };

        foreach (var directory in candidates)
        {
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (IsUsableFile(ffmpeg) && IsUsableFile(ffprobe))
                return (ffmpeg, ffprobe);
        }

        return null;
    }

    private static void ValidatePackageUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("www.gyan.dev", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith(
                "ffmpeg-release-essentials.zip",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("构建页返回了不受信任的 FFmpeg 下载地址。");
        }
    }

    private static bool IsUsableFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 1024;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyFileAtomically(string source, string destination)
    {
        var temporary = destination + ".new";
        File.Copy(source, temporary, overwrite: true);
        File.Move(temporary, destination, overwrite: true);
    }

    private static double CalculateBytesPerSecond(long bytes, TimeSpan elapsed)
        => elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
