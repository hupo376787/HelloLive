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


    public bool TryParseAuthorId(
        string responseUrl,
        string contentType,
        string responseBody,
        out string authorId)
    {
        authorId = string.Empty;
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
            return TryFindAuthorId(document.RootElement, out authorId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindAuthorId(JsonElement element, out string authorId)
    {
        authorId = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && IsAuthorContainerName(property.Name)
                    && TryReadAuthorId(property.Value, out authorId))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAuthorId(property.Value, out authorId))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAuthorId(item, out authorId))
                    return true;
            }
        }

        return false;
    }

    private static bool IsAuthorContainerName(string name)
        => name.Equals("author", StringComparison.OrdinalIgnoreCase)
           || name.Equals("user", StringComparison.OrdinalIgnoreCase)
           || name.Equals("userinfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("userInfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("owner", StringComparison.OrdinalIgnoreCase)
           || name.Equals("profile", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadAuthorId(JsonElement author, out string authorId)
    {
        foreach (var propertyName in new[]
                 {
                     "id", "eid", "userId", "user_id", "principalId", "kwaiId"
                 })
        {
            if (!TryGetPropertyIgnoreCase(author, propertyName, out var value))
                continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                authorId = text.Trim();
                return true;
            }
        }

        authorId = string.Empty;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }


    public bool TryParseAuthorName(
        string responseUrl,
        string contentType,
        string responseBody,
        out string authorName)
    {
        authorName = string.Empty;
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
            return TryFindAuthorName(document.RootElement, out authorName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindAuthorName(JsonElement element, out string authorName)
    {
        authorName = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && IsAuthorContainerName(property.Name)
                    && TryReadAuthorName(property.Value, out authorName))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAuthorName(property.Value, out authorName))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAuthorName(item, out authorName))
                    return true;
            }
        }

        return false;
    }

    private static bool TryReadAuthorName(JsonElement author, out string authorName)
    {
        foreach (var propertyName in new[]
                 {
                     "name", "userName", "username", "nickname", "nickName"
                 })
        {
            if (!TryGetPropertyIgnoreCase(author, propertyName, out var value))
                continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                authorName = text.Trim();
                return true;
            }
        }

        authorName = string.Empty;
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
