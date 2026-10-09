using System.Diagnostics;
using System.Globalization;

namespace HelloLive.Desktop.Recording;

internal sealed class StartupVideoRepairService
{
    private static readonly string[] SupportedExtensions =
    [
        ".flv",
        ".mp4"
    ];

    public async Task<StartupVideoRepairSummary> ScanAndRepairAsync(
        string downloadRoot,
        string? ffmpegPath,
        string? ffprobePath,
        Action<StartupVideoRepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot)
            || !Directory.Exists(downloadRoot))
        {
            return new StartupVideoRepairSummary(0, 0, 0, 0, 0, false);
        }

        var files = Directory
            .EnumerateFiles(downloadRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
            return new StartupVideoRepairSummary(0, 0, 0, 0, false);

        if (string.IsNullOrWhiteSpace(ffmpegPath)
            || string.IsNullOrWhiteSpace(ffprobePath)
            || !File.Exists(ffmpegPath)
            || !File.Exists(ffprobePath))
        {
            progress?.Invoke(new StartupVideoRepairProgress(
                0,
                files.Length,
                0,
                0,
                "未找到 EXE 目录中的 FFmpeg/FFprobe，已跳过录像时长修复"));
            return new StartupVideoRepairSummary(
                files.Length,
                0,
                0,
                0,
                0,
                true);
        }

        var repaired = 0;
        var healthy = 0;
        var failed = 0;
        var skippedActive = 0;

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];
            progress?.Invoke(new StartupVideoRepairProgress(
                index,
                files.Length,
                repaired,
                failed,
                $"正在检查录像 {index + 1}/{files.Length}：{Path.GetFileName(file)}"));

            if (IsFileInUse(file))
            {
                skippedActive++;
                progress?.Invoke(new StartupVideoRepairProgress(
                    index + 1,
                    files.Length,
                    repaired,
                    failed,
                    $"跳过正在使用的录像：{Path.GetFileName(file)}"));
                continue;
            }

            try
            {
                var before = await ProbeAsync(
                    ffprobePath,
                    file,
                    cancellationToken);

                if (!NeedsRepair(before))
                {
                    healthy++;
                    continue;
                }

                var temporaryPath = BuildTemporaryPath(file);
                try
                {
                    await RemuxAsync(
                        ffmpegPath,
                        file,
                        temporaryPath,
                        cancellationToken);

                    var after = await ProbeAsync(
                        ffprobePath,
                        temporaryPath,
                        cancellationToken);

                    if (!IsAcceptableAfterRepair(after))
                    {
                        failed++;
                        continue;
                    }

                    ReplaceOriginal(temporaryPath, file);
                    repaired++;
                }
                finally
                {
                    TryDeleteFile(temporaryPath);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A damaged or locked file must not prevent HelloLive from starting.
                failed++;
            }
        }

        progress?.Invoke(new StartupVideoRepairProgress(
            files.Length,
            files.Length,
            repaired,
            failed,
            $"录像检查完成：{files.Length} 个，修复 {repaired} 个，正常 {healthy} 个，跳过占用 {skippedActive} 个，失败 {failed} 个"));

        return new StartupVideoRepairSummary(
            files.Length,
            repaired,
            healthy,
            failed,
            skippedActive,
            false);
    }

    private static bool IsFileInUse(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool NeedsRepair(MediaProbe probe)
    {
        if (!probe.HasPacketTimeline)
            return false;

        if (Math.Abs(probe.FirstTimestampSeconds) > 2d)
            return true;

        if (probe.PacketDurationSeconds <= 0.25d)
            return false;

        if (probe.ReportedDurationSeconds is not { } reported
            || !double.IsFinite(reported)
            || reported <= 0d)
        {
            return true;
        }

        var tolerance = Math.Max(
            3d,
            probe.PacketDurationSeconds * 0.03d);

        return Math.Abs(reported - probe.PacketDurationSeconds) > tolerance;
    }

    private static bool IsAcceptableAfterRepair(MediaProbe probe)
    {
        if (!probe.HasPacketTimeline
            || probe.PacketDurationSeconds <= 0d
            || Math.Abs(probe.FirstTimestampSeconds) > 2d)
        {
            return false;
        }

        if (probe.ReportedDurationSeconds is not { } reported
            || !double.IsFinite(reported)
            || reported <= 0d)
        {
            return false;
        }

        var tolerance = Math.Max(
            3d,
            probe.PacketDurationSeconds * 0.03d);

        return Math.Abs(reported - probe.PacketDurationSeconds) <= tolerance;
    }

    private static async Task<MediaProbe> ProbeAsync(
        string ffprobePath,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var reportedDuration = await ReadReportedDurationAsync(
            ffprobePath,
            inputPath,
            cancellationToken);

        var (hasTimeline, first, duration) = await ReadPacketTimelineAsync(
            ffprobePath,
            inputPath,
            cancellationToken);

        return new MediaProbe(
            reportedDuration,
            hasTimeline,
            first,
            duration);
    }

    private static async Task<double?> ReadReportedDurationAsync(
        string ffprobePath,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProbeStartInfo(ffprobePath);
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(inputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return null;

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdoutTask).Trim();
        await stderrTask;

        if (process.ExitCode != 0
            || !double.TryParse(
                output,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration)
            || !double.IsFinite(duration))
        {
            return null;
        }

        return duration;
    }

    private static async Task<(bool HasTimeline, double FirstTimestamp, double Duration)>
        ReadPacketTimelineAsync(
            string ffprobePath,
            string inputPath,
            CancellationToken cancellationToken)
    {
        var startInfo = CreateProbeStartInfo(ffprobePath);
        startInfo.ArgumentList.Add("-show_packets");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("packet=pts_time,dts_time,duration_time");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("csv=p=0");
        startInfo.ArgumentList.Add(inputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return (false, 0d, 0d);

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var hasTimestamp = false;
        var minimumTimestamp = double.MaxValue;
        var maximumEnd = double.MinValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            var fields = line.Split(',');
            if (fields.Length == 0)
                continue;

            var pts = ParseProbeNumber(fields.ElementAtOrDefault(0));
            var dts = ParseProbeNumber(fields.ElementAtOrDefault(1));
            var packetDuration = ParseProbeNumber(fields.ElementAtOrDefault(2)) ?? 0d;
            var timestamp = pts ?? dts;

            if (timestamp is not { } current
                || !double.IsFinite(current))
            {
                continue;
            }

            hasTimestamp = true;
            minimumTimestamp = Math.Min(minimumTimestamp, current);
            maximumEnd = Math.Max(
                maximumEnd,
                current + Math.Max(0d, packetDuration));
        }

        await process.WaitForExitAsync(cancellationToken);
        await stderrTask;

        if (process.ExitCode != 0
            || !hasTimestamp
            || maximumEnd < minimumTimestamp)
        {
            return (false, 0d, 0d);
        }

        return (
            true,
            minimumTimestamp,
            Math.Max(0d, maximumEnd - minimumTimestamp));
    }

    private static async Task RemuxAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            RedirectStandardInput = false
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("+genpts");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v?");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a?");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-map_metadata");
        startInfo.ArgumentList.Add("-1");
        startInfo.ArgumentList.Add("-avoid_negative_ts");
        startInfo.ArgumentList.Add("make_zero");

        if (Path.GetExtension(outputPath)
            .Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-movflags");
            startInfo.ArgumentList.Add("+faststart");
        }

        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 FFmpeg 修复录像。");

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        var error = await stderrTask;
        if (process.ExitCode != 0
            || !File.Exists(outputPath)
            || new FileInfo(outputPath).Length <= 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"FFmpeg 修复失败，退出代码：{process.ExitCode}"
                    : error.Trim());
        }
    }

    private static ProcessStartInfo CreateProbeStartInfo(string ffprobePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        return startInfo;
    }

    private static double? ParseProbeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number)
            && double.IsFinite(number)
                ? number
                : null;
    }

    private static string BuildTemporaryPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath)
                        ?? throw new InvalidOperationException("录像路径无父目录。");
        var extension = Path.GetExtension(originalPath);
        var name = Path.GetFileNameWithoutExtension(originalPath);

        return Path.Combine(
            directory,
            $".{name}.hellolive-repair-{Guid.NewGuid():N}{extension}");
    }

    private static void ReplaceOriginal(
        string temporaryPath,
        string originalPath)
    {
        try
        {
            File.Replace(
                temporaryPath,
                originalPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }

        var backupPath = originalPath
                         + ".hellolive-backup-"
                         + Guid.NewGuid().ToString("N");

        File.Move(originalPath, backupPath);

        try
        {
            File.Move(temporaryPath, originalPath);
            TryDeleteFile(backupPath);
        }
        catch
        {
            if (!File.Exists(originalPath) && File.Exists(backupPath))
                File.Move(backupPath, originalPath);

            throw;
        }
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

    private sealed record MediaProbe(
        double? ReportedDurationSeconds,
        bool HasPacketTimeline,
        double FirstTimestampSeconds,
        double PacketDurationSeconds);
}

internal sealed record StartupVideoRepairProgress(
    int ProcessedCount,
    int TotalCount,
    int RepairedCount,
    int FailedCount,
    string Message);

internal sealed record StartupVideoRepairSummary(
    int TotalCount,
    int RepairedCount,
    int HealthyCount,
    int FailedCount,
    int SkippedActiveCount,
    bool SkippedBecauseFfmpegMissing);
