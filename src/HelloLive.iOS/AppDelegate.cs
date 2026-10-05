using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace HelloLive.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<HelloLive.Core.Remote.RemoteApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder);
}
