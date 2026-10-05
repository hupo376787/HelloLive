using Android.App;
using Android.Runtime;
using Avalonia.Android;
using HelloLive.Core.Remote;

namespace HelloLive.Android;

[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<RemoteApp>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
