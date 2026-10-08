using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace HelloLive.Core.Models;

public enum LiveMonitorState
{
    Idle,
    Checking,
    Live,
    NotDetected,
    Error
}

public sealed class LiveMonitorTarget : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _platformId = "kuaishou";
    private string _displayName = string.Empty;
    private bool _useCustomDisplayName;
    private string _authorId = string.Empty;
    private string _profileUrl = string.Empty;
    private string? _avatarUrl;
    private bool _useCustomAvatar;
    private IImage? _avatarImage;
    private bool _isEnabled = true;
    private LiveMonitorState _state = LiveMonitorState.Idle;
    private string _statusMessage = "等待检查";
    private DateTimeOffset? _lastCheckedAt;
    private string? _streamUrl;
    private string? _streamFormat;
    private string? _resolvedPageUrl;
    private bool _isRecording;
    private string? _recordingFilePath;
    private string _recordingStatus = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    public string PlatformId
    {
        get => _platformId;
        set
        {
            if (SetProperty(ref _platformId, value ?? string.Empty))
                OnPropertyChanged(nameof(PlatformText));
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value ?? string.Empty))
                OnPropertyChanged(nameof(AvatarInitial));
        }
    }

    public bool UseCustomDisplayName
    {
        get => _useCustomDisplayName;
        set => SetProperty(ref _useCustomDisplayName, value);
    }

    public string AuthorId
    {
        get => _authorId;
        set
        {
            if (SetProperty(ref _authorId, value ?? string.Empty))
                OnPropertyChanged(nameof(AuthorIdText));
        }
    }

    [JsonIgnore]
    public string AuthorIdText => string.IsNullOrWhiteSpace(AuthorId)
        ? "作者ID：待解析"
        : $"作者ID：{AuthorId}";

    public string ProfileUrl
    {
        get => _profileUrl;
        set => SetProperty(ref _profileUrl, value ?? string.Empty);
    }

    public string? AvatarUrl
    {
        get => _avatarUrl;
        set => SetProperty(ref _avatarUrl, value);
    }

    public bool UseCustomAvatar
    {
        get => _useCustomAvatar;
        set => SetProperty(ref _useCustomAvatar, value);
    }

    [JsonIgnore]
    public IImage? AvatarImage
    {
        get => _avatarImage;
        set
        {
            if (SetProperty(ref _avatarImage, value))
                OnPropertyChanged(nameof(HasAvatarImage));
        }
    }

    [JsonIgnore]
    public bool HasAvatarImage => AvatarImage is not null;

    [JsonIgnore]
    public string AvatarInitial
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
                return "L";

            var enumerator = StringInfo.GetTextElementEnumerator(DisplayName.Trim());
            return enumerator.MoveNext()
                ? enumerator.GetTextElement()
                : "L";
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    [JsonIgnore]
    public LiveMonitorState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateIcon));
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(HasStreamUrl));
            }
        }
    }

    [JsonIgnore]
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value ?? string.Empty);
    }

    [JsonIgnore]
    public DateTimeOffset? LastCheckedAt
    {
        get => _lastCheckedAt;
        set
        {
            if (SetProperty(ref _lastCheckedAt, value))
                OnPropertyChanged(nameof(LastCheckedText));
        }
    }

    [JsonIgnore]
    public string? StreamUrl
    {
        get => _streamUrl;
        set
        {
            if (SetProperty(ref _streamUrl, value))
                OnPropertyChanged(nameof(HasStreamUrl));
        }
    }

    [JsonIgnore]
    public string? StreamFormat
    {
        get => _streamFormat;
        set => SetProperty(ref _streamFormat, value);
    }

    [JsonIgnore]
    public string? ResolvedPageUrl
    {
        get => _resolvedPageUrl;
        set => SetProperty(ref _resolvedPageUrl, value);
    }

    [JsonIgnore]
    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    [JsonIgnore]
    public string? RecordingFilePath
    {
        get => _recordingFilePath;
        set
        {
            if (SetProperty(ref _recordingFilePath, value))
                OnPropertyChanged(nameof(RecordingFileName));
        }
    }

    [JsonIgnore]
    public string RecordingStatus
    {
        get => _recordingStatus;
        set => SetProperty(ref _recordingStatus, value ?? string.Empty);
    }

    [JsonIgnore]
    public string? RecordingFileName => string.IsNullOrWhiteSpace(RecordingFilePath)
        ? null
        : Path.GetFileName(RecordingFilePath);

    [JsonIgnore]
    public string PlatformText => PlatformId.ToLowerInvariant() switch
    {
        "kuaishou" => "快手",
        "douyin" => "抖音",
        _ => PlatformId
    };

    [JsonIgnore]
    public bool IsLive => State == LiveMonitorState.Live;

    [JsonIgnore]
    public bool HasStreamUrl => !string.IsNullOrWhiteSpace(StreamUrl);

    [JsonIgnore]
    public string StateText => State switch
    {
        LiveMonitorState.Checking => "检查中",
        LiveMonitorState.Live => "直播中",
        LiveMonitorState.NotDetected => "未检测到直播",
        LiveMonitorState.Error => "检查失败",
        _ => "等待检查"
    };

    [JsonIgnore]
    public string StateIcon => State switch
    {
        LiveMonitorState.Checking => "◌",
        LiveMonitorState.Live => "●",
        LiveMonitorState.NotDetected => "○",
        LiveMonitorState.Error => "!",
        _ => "·"
    };

    [JsonIgnore]
    public string LastCheckedText => LastCheckedAt is { } value
        ? $"上次检查：{value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
        : "尚未检查";

    public LiveMonitorTargetSnapshot ToSnapshot()
        => new(Id, PlatformId, DisplayName, AuthorId, ProfileUrl, IsEnabled);

    public void ApplyResult(LiveCheckResult result)
    {
        State = result.State;
        StatusMessage = result.Message;
        LastCheckedAt = result.CheckedAt;
        StreamUrl = result.Stream?.Url;
        StreamFormat = result.Stream?.Format;
        ResolvedPageUrl = result.ResolvedPageUrl;
        if (!string.IsNullOrWhiteSpace(result.AuthorId))
            AuthorId = result.AuthorId;

        if (!UseCustomDisplayName
            && !string.IsNullOrWhiteSpace(result.AuthorName))
        {
            DisplayName = result.AuthorName;
        }

        if (!UseCustomAvatar
            && !string.IsNullOrWhiteSpace(result.AvatarUrl))
        {
            AvatarUrl = result.AvatarUrl;
        }
    }

    public void ApplyRecordingState(LiveRecordingState state)
    {
        IsRecording = state.IsRecording;
        RecordingFilePath = state.FilePath;
        RecordingStatus = state.Message;
    }
}

public sealed record LiveMonitorTargetSnapshot(
    string Id,
    string PlatformId,
    string DisplayName,
    string AuthorId,
    string ProfileUrl,
    bool IsEnabled);

public sealed record LiveStreamInfo(
    string Url,
    string Format,
    string? Quality = null,
    string? Source = null,
    string? RefererUrl = null,
    string? Origin = null,
    string? UserAgent = null,
    string? AuthorName = null);

public sealed record LiveCheckResult(
    string TargetId,
    LiveMonitorState State,
    string Message,
    DateTimeOffset CheckedAt,
    LiveStreamInfo? Stream = null,
    string? ResolvedPageUrl = null,
    string? AuthorId = null,
    string? AuthorName = null,
    string? AvatarUrl = null)
{
    public static LiveCheckResult Checking(string id)
        => new(id, LiveMonitorState.Checking, "正在检查直播状态…", DateTimeOffset.Now);

    public static LiveCheckResult Live(
        string id,
        LiveStreamInfo stream,
        string? resolvedPageUrl,
        string? authorId = null,
        string? authorName = null,
        string? avatarUrl = null)
        => new(
            id,
            LiveMonitorState.Live,
            $"已发现 {stream.Format} 直播流",
            DateTimeOffset.Now,
            stream,
            resolvedPageUrl,
            authorId,
            authorName,
            avatarUrl);

    public static LiveCheckResult NotDetected(
        string id,
        string? resolvedPageUrl,
        string? authorId = null,
        string? authorName = null,
        string? avatarUrl = null)
        => new(
            id,
            LiveMonitorState.NotDetected,
            "本次页面检查未发现直播流",
            DateTimeOffset.Now,
            null,
            resolvedPageUrl,
            authorId,
            authorName,
            avatarUrl);

    public static LiveCheckResult Error(string id, string message, string? resolvedPageUrl = null)
        => new(id, LiveMonitorState.Error, message, DateTimeOffset.Now, null, resolvedPageUrl);
}

public sealed record LiveRecordingState(
    string TargetId,
    bool IsRecording,
    string? FilePath,
    string Message,
    DateTimeOffset ChangedAt);

public sealed record LiveMonitorOptions(
    bool Headless,
    int MaxConcurrency,
    int CheckIntervalSeconds,
    int CheckTimeoutSeconds,
    bool BlockImagesAndFonts)
{
    public LiveMonitorOptions Normalize()
        => this with
        {
            MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 8),
            CheckIntervalSeconds = Math.Clamp(CheckIntervalSeconds, 10, 3600),
            CheckTimeoutSeconds = Math.Clamp(CheckTimeoutSeconds, 5, 120)
        };
}
