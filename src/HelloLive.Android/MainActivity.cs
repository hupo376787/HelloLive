using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace HelloLive.Android;

[Activity(
    Label = "HelloLive Remote",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation
                           | ConfigChanges.ScreenSize
                           | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
