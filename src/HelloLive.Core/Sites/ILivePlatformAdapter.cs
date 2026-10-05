using HelloLive.Core.Models;

namespace HelloLive.Core.Sites;

/// <summary>
/// 直播平台适配器只描述平台特征，不依赖 Playwright。
/// 后续分析抖音、B站等平台时，只需新增适配器并注册到 LivePlatformRegistry。
/// </summary>
public interface ILivePlatformAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string HomeUrl { get; }

    bool CanHandleProfileUrl(string url);

    /// <summary>把用户输入标准化为可以交给浏览器导航的地址。</summary>
    string NormalizeProfileUrl(string url);

    /// <summary>
    /// 判断浏览器即将发出的请求是否为真实直播流。返回 true 时浏览器层会保存 URL，
    /// 并可立即中止媒体下载，避免直播页面长期占用带宽和解码资源。
    /// </summary>
    bool TryParseStreamRequest(
        string requestUrl,
        string resourceType,
        out LiveStreamInfo stream);

    /// <summary>
    /// 某些平台先在 JSON 接口中返回播放地址，再由播放器请求媒体。
    /// 浏览器层只会把体积较小的文本/JSON 响应交给这里分析。
    /// </summary>
    bool TryParseApiResponse(
        string responseUrl,
        string contentType,
        string responseBody,
        out LiveStreamInfo stream);

    /// <summary>
    /// 为以后接入低资源占用的 HttpClient 直连查询保留入口。
    /// 返回 null 表示当前适配器仍需使用浏览器检查。
    /// </summary>
    Task<LiveCheckResult?> TryCheckDirectAsync(
        HttpClient httpClient,
        LiveMonitorTargetSnapshot target,
        LiveMonitorOptions options,
        CancellationToken cancellationToken)
        => Task.FromResult<LiveCheckResult?>(null);
}
