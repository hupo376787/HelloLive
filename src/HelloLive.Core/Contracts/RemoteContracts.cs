namespace HelloLive.Core.Contracts;

public sealed class RemoteHealthDto
{
    public string Service { get; set; } = "HelloLive";
    public string Version { get; set; } = "1.0";
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.Now;
}

public sealed class RemoteLiveSnapshot
{
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.Now;
    public bool IsMonitoring { get; set; }
    public string CurrentTask { get; set; } = string.Empty;
    public int MonitorCount { get; set; }
    public int EnabledCount { get; set; }
    public int LiveCount { get; set; }
    public int NotDetectedCount { get; set; }
    public int CheckingCount { get; set; }
    public int FailedCount { get; set; }
    public List<RemoteMonitorDto> Monitors { get; set; } = new();
    public List<string> Logs { get; set; } = new();
}

public sealed class RemoteMonitorDto
{
    public string Id { get; set; } = string.Empty;
    public string PlatformId { get; set; } = string.Empty;
    public string PlatformText { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string StateText { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public string LastCheckedText { get; set; } = string.Empty;
    public string? StreamFormat { get; set; }
    public string? StreamUrl { get; set; }
}

public sealed class RemoteAddMonitorRequest
{
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class RemoteMonitorEnabledRequest
{
    public bool IsEnabled { get; set; }
}

public sealed class RemoteCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public static RemoteCommandResult Ok(string message)
        => new() { Success = true, Message = message };

    public static RemoteCommandResult Fail(string message)
        => new() { Success = false, Message = message };
}
