namespace HelloLive.Core.Remote.Services;

public interface IRemoteClientPreferencesStore
{
    RemoteClientPreferences Load();
    void Save(RemoteClientPreferences preferences);
}

public sealed class RemoteClientPreferences
{
    public string ServerAddress { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public bool IsDarkTheme { get; init; } = true;
}
