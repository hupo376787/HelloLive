namespace HelloLive.Core.Services.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 4;
    public string Theme { get; set; } = "Light";
    public bool HeadlessMode { get; set; } = true;
    public int MaxConcurrentPages { get; set; } = 4;
    public int CheckIntervalSeconds { get; set; } = 300;
    public int CheckTimeoutSeconds { get; set; } = 20;
    public bool BlockImagesAndFonts { get; set; } = true;
    public bool AutoStartMonitoring { get; set; }
    public bool MonitorPanelVisible { get; set; } = true;

    /// <summary>录像结束后使用 FFmpeg 转码为更小的 MP4 文件。</summary>
    public bool CompressAfterRecording { get; set; }

    /// <summary>仅在压缩成功且压缩文件更小时删除原录像。</summary>
    public bool DeleteOriginalAfterCompression { get; set; }

    /// <summary>PushPlus 微信开播提醒 Token；为空时不发送开播通知。</summary>
    public string PushPlusToken { get; set; } = string.Empty;

    // Desktop remote-control host. Android/iOS/Browser are controllers only.
    public bool RemoteApiEnabled { get; set; }
    public int RemoteApiPort { get; set; } = 5088;
    public string RemoteApiToken { get; set; } = string.Empty;
}
