using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace HelloLive.Core.Services.Settings;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsService()
    {
        var portablePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        SettingsPath = OperatingSystem.IsWindows()
            ? portablePath
            : Path.Combine(GetApplicationDataDirectory(), "settings.json");
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return Normalize(new AppSettings());

            var json = File.ReadAllText(SettingsPath);
            return Normalize(JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings());
        }
        catch
        {
            return Normalize(new AppSettings());
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = Normalize(settings);
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = SettingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, SettingsPath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Version = 2;
        settings.MaxConcurrentPages = Math.Clamp(settings.MaxConcurrentPages, 1, 8);
        settings.CheckIntervalSeconds = Math.Clamp(settings.CheckIntervalSeconds, 10, 3600);
        settings.CheckTimeoutSeconds = Math.Clamp(settings.CheckTimeoutSeconds, 5, 120);
        settings.RemoteApiPort = Math.Clamp(settings.RemoteApiPort, 1024, 65535);
        settings.Theme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "Dark"
            : "Light";

        if (string.IsNullOrWhiteSpace(settings.RemoteApiToken))
            settings.RemoteApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        return settings;
    }

    private static string GetApplicationDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        var directory = Path.Combine(root, "HelloLive");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
