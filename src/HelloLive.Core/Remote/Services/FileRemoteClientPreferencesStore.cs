using System.Text;

namespace HelloLive.Core.Remote.Services;

public sealed class FileRemoteClientPreferencesStore : IRemoteClientPreferencesStore
{
    private readonly string _filePath;

    public FileRemoteClientPreferencesStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? BuildDefaultPath()
            : filePath;
    }

    public RemoteClientPreferences Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new RemoteClientPreferences();

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            return new RemoteClientPreferences
            {
                ServerAddress = Decode(values.GetValueOrDefault("server")),
                AccessToken = Decode(values.GetValueOrDefault("token")),
                IsDarkTheme = !string.Equals(
                    values.GetValueOrDefault("theme"),
                    "Light",
                    StringComparison.OrdinalIgnoreCase)
            };
        }
        catch
        {
            return new RemoteClientPreferences();
        }
    }

    public void Save(RemoteClientPreferences preferences)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temp = _filePath + ".tmp";
            File.WriteAllLines(
                temp,
                [
                    "version=1",
                    $"server={Encode(preferences.ServerAddress)}",
                    $"token={Encode(preferences.AccessToken)}",
                    $"theme={(preferences.IsDarkTheme ? "Dark" : "Light")}"
                ],
                new UTF8Encoding(false));
            File.Move(temp, _filePath, true);
        }
        catch
        {
            // Remote settings persistence must never prevent the controller from running.
        }
    }

    private static string BuildDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        var directory = Path.Combine(root, "HelloLiveRemote");
        return Path.Combine(directory, "remote-client.settings");
    }

    private static string Encode(string? value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return string.Empty;
        }
    }
}
