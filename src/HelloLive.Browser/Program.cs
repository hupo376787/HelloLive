using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Logging;
using HelloLive.Core.Remote.Services;

[assembly: SupportedOSPlatform("browser")]

namespace HelloLive.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());

        RemoteClientPreferencesStoreProvider.Current =
            new BrowserRemoteClientPreferencesStore();

        var browserOptions = new BrowserPlatformOptions
        {
            RenderingMode =
            [
                BrowserRenderingMode.WebGL2,
                BrowserRenderingMode.WebGL1,
                BrowserRenderingMode.Software2D
            ]
        };

        await BuildAvaloniaApp()
            .WithFont_SourceHanSansCN()
            .LogToTrace()
            .StartBrowserAppAsync("out", browserOptions);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<HelloLive.Core.Remote.RemoteApp>();
}
