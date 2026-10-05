namespace HelloLive.Core.Services.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public string Theme { get; set; } = "Light";
    public bool HeadlessMode { get; set; } = true;
    public int MaxConcurrentPages { get; set; } = 4;
    public int CheckIntervalSeconds { get; set; } = 60;
    public int CheckTimeoutSeconds { get; set; } = 20;
    public bool BlockImagesAndFonts { get; set; } = true;
    public bool AutoStartMonitoring { get; set; }
    public bool MonitorPanelVisible { get; set; } = true;

    // Desktop remote-control host. Android/iOS/Browser are controllers only.
    public bool RemoteApiEnabled { get; set; }
    public int RemoteApiPort { get; set; } = 5088;
    public string RemoteApiToken { get; set; } = string.Empty;
}
