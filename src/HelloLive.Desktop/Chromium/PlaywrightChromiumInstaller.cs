using HelloLive.Core.Services.Browser;
using System.Text;
using System.Text.RegularExpressions;

namespace HelloLive.Desktop.Chromium;

public sealed class PlaywrightChromiumInstaller
{
    private const string BrowsersPathEnvironmentVariable = "PLAYWRIGHT_BROWSERS_PATH";
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public string PreferredInstallDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "chromium"));

    public async Task<int> InstallAsync(
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            EnsureInstallDirectoryWritable();
            progress?.Report(new ChromiumInstallProgress(null, "Chromium"));

            var previousBrowsersPath = Environment.GetEnvironmentVariable(
                BrowsersPathEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            var originalOut = Console.Out;
            var originalError = Console.Error;

            using var outputWriter = new InstallProgressWriter(originalOut, progress);
            using var errorWriter = new InstallProgressWriter(originalError, progress);

            try
            {
                Environment.SetEnvironmentVariable(
                    BrowsersPathEnvironmentVariable,
                    PreferredInstallDirectory,
                    EnvironmentVariableTarget.Process);
                Console.SetOut(outputWriter);
                Console.SetError(errorWriter);

                var exitCode = await Task.Run(
                    () => Microsoft.Playwright.Program.Main(["install", "chromium"]),
                    cancellationToken);

                outputWriter.FlushPending();
                errorWriter.FlushPending();
                return exitCode;
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Environment.SetEnvironmentVariable(
                    BrowsersPathEnvironmentVariable,
                    previousBrowsersPath,
                    EnvironmentVariableTarget.Process);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    public async Task<string?> FindInstalledExecutablePathAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var executablePath = FindChromiumExecutable(PreferredInstallDirectory);
        if (!string.IsNullOrWhiteSpace(executablePath))
            return executablePath;

        var defaultDirectory = GetDefaultPlaywrightBrowsersDirectory();
        executablePath = FindChromiumExecutable(defaultDirectory);
        if (!string.IsNullOrWhiteSpace(executablePath))
            return executablePath;

        var customDirectory = Environment.GetEnvironmentVariable(
            BrowsersPathEnvironmentVariable,
            EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(customDirectory))
        {
            executablePath = FindChromiumExecutable(customDirectory);
            if (!string.IsNullOrWhiteSpace(executablePath))
                return executablePath;
        }

        try
        {
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var reportedPath = playwright.Chromium.ExecutablePath;
            return !string.IsNullOrWhiteSpace(reportedPath) && File.Exists(reportedPath)
                ? Path.GetFullPath(reportedPath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureInstallDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(PreferredInstallDirectory);
            var testFile = Path.Combine(PreferredInstallDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(testFile)) { }
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法在 {PreferredInstallDirectory} 安装 Chromium。请把 HelloLive 放到当前用户可写目录。",
                ex);
        }
    }

    private static string? FindChromiumExecutable(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return null;

        try
        {
            var fullRoot = Path.GetFullPath(rootDirectory);
            if (!Directory.Exists(fullRoot))
                return null;

            var expectedNames = OperatingSystem.IsWindows()
                ? new[] { "chrome.exe" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "Chromium", "Google Chrome for Testing" }
                    : new[] { "chrome", "chromium" };

            return expectedNames
                .SelectMany(name => Directory.EnumerateFiles(fullRoot, name, SearchOption.AllDirectories))
                .Where(path => !path.Contains("headless_shell", StringComparison.OrdinalIgnoreCase)
                               && !path.Contains("chrome-headless-shell", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => Path.GetFullPath(file.FullName))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDefaultPlaywrightBrowsersDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "ms-playwright");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return null;

        return OperatingSystem.IsMacOS()
            ? Path.Combine(userProfile, "Library", "Caches", "ms-playwright")
            : Path.Combine(userProfile, ".cache", "ms-playwright");
    }

    private sealed class InstallProgressWriter : TextWriter
    {
        private static readonly Regex PercentRegex = new(
            @"(?<!\d)(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly TextWriter _inner;
        private readonly IProgress<ChromiumInstallProgress>? _progress;
        private readonly StringBuilder _line = new();
        private readonly object _sync = new();

        public InstallProgressWriter(TextWriter inner, IProgress<ChromiumInstallProgress>? progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_sync)
            {
                _inner.Write(value);
                Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            lock (_sync)
            {
                _inner.Write(value);
                foreach (var c in value)
                    Append(c);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                _inner.WriteLine(value);
                if (!string.IsNullOrEmpty(value))
                {
                    foreach (var c in value)
                        Append(c);
                }
                Append('\n');
            }
        }

        public void FlushPending()
        {
            lock (_sync)
            {
                ReportLine(_line.ToString());
                _line.Clear();
                _inner.Flush();
            }
        }

        private void Append(char value)
        {
            if (value is '\r' or '\n')
            {
                ReportLine(_line.ToString());
                _line.Clear();
                return;
            }
            if (_line.Length < 4096)
                _line.Append(value);
        }

        private void ReportLine(string raw)
        {
            if (_progress is null)
                return;

            var line = raw.Trim();
            if (line.Length == 0)
                return;

            var match = PercentRegex.Match(line);
            if (match.Success
                && double.TryParse(match.Groups["percent"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var percent))
            {
                _progress.Report(new ChromiumInstallProgress(Math.Clamp(percent, 0, 100), "Chromium"));
            }
            else if (line.Contains("download", StringComparison.OrdinalIgnoreCase))
            {
                _progress.Report(new ChromiumInstallProgress(null, "正在下载 Chromium"));
            }
            else if (line.Contains("extract", StringComparison.OrdinalIgnoreCase))
            {
                _progress.Report(new ChromiumInstallProgress(null, "正在解压 Chromium"));
            }
        }
    }
}
