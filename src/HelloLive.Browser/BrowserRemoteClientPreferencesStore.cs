using System.Runtime.InteropServices.JavaScript;
using HelloLive.Core.Remote.Services;

namespace HelloLive.Browser;

internal sealed class BrowserRemoteClientPreferencesStore : IRemoteClientPreferencesStore
{
    private const string ServerKey = "HelloLive.Remote.ServerAddress";
    private const string TokenKey = "HelloLive.Remote.AccessToken";
    private const string ThemeKey = "HelloLive.Remote.Theme";

    public RemoteClientPreferences Load()
    {
        try
        {
            return new RemoteClientPreferences
            {
                ServerAddress = BrowserStorageInterop.GetItem(ServerKey) ?? string.Empty,
                AccessToken = BrowserStorageInterop.GetItem(TokenKey) ?? string.Empty,
                IsDarkTheme = !string.Equals(
                    BrowserStorageInterop.GetItem(ThemeKey),
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
            BrowserStorageInterop.SetItem(ServerKey, preferences.ServerAddress ?? string.Empty);
            BrowserStorageInterop.SetItem(TokenKey, preferences.AccessToken ?? string.Empty);
            BrowserStorageInterop.SetItem(ThemeKey, preferences.IsDarkTheme ? "Dark" : "Light");
        }
        catch
        {
        }
    }
}

internal static partial class BrowserStorageInterop
{
    [JSImport("globalThis.helloLiveRemoteStorageGetItem")]
    internal static partial string? GetItem(string key);

    [JSImport("globalThis.helloLiveRemoteStorageSetItem")]
    internal static partial void SetItem(string key, string value);
}
