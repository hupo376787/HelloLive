using HelloLive.Core.Models;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace HelloLive.Core.Services.Monitoring;

public sealed class MonitorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public MonitorStore(string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
        StorePath = Path.Combine(directory, "monitors.json");
    }

    public string StorePath { get; }

    public IReadOnlyList<LiveMonitorTarget> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return Array.Empty<LiveMonitorTarget>();

            var json = File.ReadAllText(StorePath);
            var items = JsonSerializer.Deserialize<List<LiveMonitorTarget>>(json, JsonOptions);
            return items is null ? Array.Empty<LiveMonitorTarget>() : items;
        }
        catch
        {
            return Array.Empty<LiveMonitorTarget>();
        }
    }

    public async Task SaveAsync(
        IEnumerable<LiveMonitorTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var snapshot = targets.ToArray();
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tempPath = StorePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, StorePath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
