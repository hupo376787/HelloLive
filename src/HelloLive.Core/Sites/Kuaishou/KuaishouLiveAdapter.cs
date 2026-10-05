using HelloLive.Core.Models;
using System.Text.Json;

namespace HelloLive.Core.Sites.Kuaishou;

public sealed class KuaishouLiveAdapter : ILivePlatformAdapter
{
    private static readonly string[] ProfileHosts =
    [
        "live.kuaishou.com",
        "www.kuaishou.com",
        "kuaishou.com",
        "v.kuaishou.com",
        "v.m.chenzhongtech.com",
        "m.gifshow.com"
    ];

    private static readonly string[] StreamHostSuffixes =
    [
        ".yximgs.com",
        ".kwaicdn.com",
        ".kuaishou.com"
    ];

    public string Id => "kuaishou";
    public string DisplayName => "快手";
    public string HomeUrl => "https://live.kuaishou.com/";

    public bool CanHandleProfileUrl(string url)
    {
        if (!TryCreateUri(url, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return ProfileHosts.Any(x => host.Equals(x, StringComparison.OrdinalIgnoreCase))
               || host.EndsWith(".kuaishou.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".chenzhongtech.com", StringComparison.OrdinalIgnoreCase);
    }

    public string NormalizeProfileUrl(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0)
            return value;

        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            return value;

        return "https://" + value.TrimStart('/');
    }

    public bool TryParseStreamRequest(
        string requestUrl,
        string resourceType,
        out LiveStreamInfo stream)
    {
        stream = null!;
        if (!TryCreateUri(requestUrl, out var uri))
            return false;

        if (!IsKnownStreamHost(uri.Host))
            return false;

        var format = GetFormat(uri.AbsolutePath);
        if (format is null)
            return false;

        stream = new LiveStreamInfo(
            requestUrl,
            format,
            GuessQuality(uri.AbsolutePath),
            "network-request");
        return true;
    }

    public bool TryParseApiResponse(
        string responseUrl,
        string contentType,
        string responseBody,
        out LiveStreamInfo stream)
    {
        stream = null!;
        if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Length > 2_000_000)
            return false;

        if (!responseUrl.Contains("kuaishou", StringComparison.OrdinalIgnoreCase)
            && !responseUrl.Contains("gifshow", StringComparison.OrdinalIgnoreCase)
            && !responseUrl.Contains("chenzhongtech", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = responseBody.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            foreach (var value in EnumerateStrings(document.RootElement))
            {
                if (!TryCreateUri(value, out var uri) || !IsKnownStreamHost(uri.Host))
                    continue;

                var format = GetFormat(uri.AbsolutePath);
                if (format is null)
                    continue;

                stream = new LiveStreamInfo(
                    value,
                    format,
                    GuessQuality(uri.AbsolutePath),
                    "api-response");
                return true;
            }
        }
        catch (JsonException)
        {
            // 平台返回的并非标准 JSON 时忽略，等待真实媒体请求。
        }

        return false;
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
                yield break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                foreach (var child in EnumerateStrings(item))
                    yield return child;
                yield break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                foreach (var child in EnumerateStrings(property.Value))
                    yield return child;
                yield break;
        }
    }

    private static bool IsKnownStreamHost(string host)
    {
        var normalized = host.ToLowerInvariant();
        return StreamHostSuffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetFormat(string path)
    {
        if (path.EndsWith(".flv", StringComparison.OrdinalIgnoreCase))
            return "HTTP-FLV";
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return "HLS";
        return null;
    }

    private static string? GuessQuality(string path)
    {
        if (path.Contains("Hd", StringComparison.OrdinalIgnoreCase))
            return "HD";
        if (path.Contains("Sd", StringComparison.OrdinalIgnoreCase))
            return "SD";
        return null;
    }

    private static bool TryCreateUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
