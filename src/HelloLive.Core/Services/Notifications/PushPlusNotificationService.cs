using HelloLive.Core.Models;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace HelloLive.Core.Services.Notifications;

public sealed class PushPlusNotificationService : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task SendLiveStartedAsync(
        string token,
        LiveMonitorTarget target,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        ArgumentNullException.ThrowIfNull(target);

        var authorName = string.IsNullOrWhiteSpace(target.DisplayName)
            ? "未知作者"
            : target.DisplayName.Trim();
        var authorId = string.IsNullOrWhiteSpace(target.AuthorId)
            ? "待解析"
            : target.AuthorId.Trim();
        var profileUrl = target.ProfileUrl?.Trim() ?? string.Empty;
        var avatarUrl = target.AvatarUrl?.Trim() ?? string.Empty;
        var currentVersion = GetCurrentVersion();

        var title = $"HelloLive({authorName})开始直播";
        var linkHtml = string.IsNullOrWhiteSpace(profileUrl)
            ? string.Empty
            : $"<a href=\"{WebUtility.HtmlEncode(profileUrl)}\">{WebUtility.HtmlEncode(profileUrl)}</a>";
        var imageHtml = string.IsNullOrWhiteSpace(avatarUrl)
            ? string.Empty
            : $"<br><img src=\"{WebUtility.HtmlEncode(avatarUrl)}\">";

        var content =
            $"作者：{WebUtility.HtmlEncode(authorName)}（{WebUtility.HtmlEncode(authorId)}）<br>" +
            $"检测到开播时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}<br>" +
            $"主页：{linkHtml}<br>" +
            $"HelloLive V{WebUtility.HtmlEncode(currentVersion)}{imageHtml}";

        var requestUrl =
            "https://www.pushplus.plus/send" +
            $"?token={Uri.EscapeDataString(token.Trim())}" +
            $"&title={Uri.EscapeDataString(title)}" +
            $"&content={Uri.EscapeDataString(content)}";

        using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"PushPlus HTTP {(int)response.StatusCode} {response.ReasonPhrase}：" +
                TrimResponse(responseText));
        }

        if (TryReadPushPlusError(responseText, out var error))
            throw new InvalidOperationException(error);
    }

    private static bool TryReadPushPlusError(string json, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var code = 0;
            if (root.TryGetProperty("code", out var codeElement))
            {
                if (codeElement.ValueKind == JsonValueKind.Number)
                    codeElement.TryGetInt32(out code);
                else
                    int.TryParse(codeElement.ToString(), out code);
            }

            if (code is 0 or 200)
                return false;

            var message = root.TryGetProperty("msg", out var msgElement)
                ? msgElement.ToString()
                : root.TryGetProperty("message", out var messageElement)
                    ? messageElement.ToString()
                    : "未知错误";

            error = $"PushPlus 返回失败（code={code}）：{message}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }

    private static string TrimResponse(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240
            ? normalized
            : normalized[..240] + "…";
    }

    public void Dispose() => _httpClient.Dispose();
}
