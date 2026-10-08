using HelloLive.Core.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    private static readonly HttpClient RedirectClient = CreateRedirectClient();

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

    public async Task<string> ResolveProfileUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProfileUrl(url);
        if (!TryCreateUri(normalized, out var originalUri))
            return normalized;

        Uri resolvedUri = originalUri;
        string? responseBody = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, originalUri);
            using var response = await RedirectClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            resolvedUri = response.RequestMessage?.RequestUri ?? originalUri;

            // Most Kuaishou share/short links resolve through ordinary HTTP redirects.
            if (!TryExtractProfileIdentifier(resolvedUri, out _))
            {
                responseBody = await ReadLimitedTextAsync(
                    response.Content,
                    512 * 1024,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Network resolution is best-effort. Direct profile URLs can still be
            // canonicalized locally if the profile/principal id is present in the path.
        }

        if (TryExtractProfileIdentifier(resolvedUri, out var profileId)
            || TryExtractProfileIdentifier(originalUri, out profileId)
            || TryExtractProfileIdentifierFromBody(responseBody, out profileId))
        {
            return BuildCanonicalProfileUrl(profileId);
        }

        return resolvedUri.AbsoluteUri;
    }

    private static HttpClient CreateRedirectClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/141.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    private static string BuildCanonicalProfileUrl(string profileId)
        => $"https://live.kuaishou.com/profile/{Uri.EscapeDataString(profileId.Trim())}";

    private static bool TryExtractProfileIdentifier(
        Uri uri,
        out string profileId)
    {
        profileId = string.Empty;

        var segments = uri.AbsolutePath
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("profile", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("u", StringComparison.OrdinalIgnoreCase))
            {
                return TryAcceptProfileIdentifier(
                    Uri.UnescapeDataString(segments[i + 1]),
                    out profileId);
            }

            if (segments[i].Equals("fw", StringComparison.OrdinalIgnoreCase)
                && i + 2 < segments.Length
                && segments[i + 1].Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                return TryAcceptProfileIdentifier(
                    Uri.UnescapeDataString(segments[i + 2]),
                    out profileId);
            }
        }

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?')
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = Uri.UnescapeDataString(pair[..separator]);
                if (!key.Equals("authorId", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("userId", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("principalId", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("profileId", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
                if (TryAcceptProfileIdentifier(value, out profileId))
                    return true;
            }
        }

        return false;
    }

    private static bool TryExtractProfileIdentifierFromBody(
        string? body,
        out string profileId)
    {
        profileId = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        // JSON often escapes '/' as '\/'. Normalizing it first also lets the same
        // expression handle ordinary HTML links.
        var normalized = body.Replace("\\/", "/", StringComparison.Ordinal);
        var match = Regex.Match(
            normalized,
            @"(?:https?://(?:live|www)\.kuaishou\.com)?/profile/([A-Za-z0-9_-]{3,128})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success
               && TryAcceptProfileIdentifier(match.Groups[1].Value, out profileId);
    }

    private static bool TryAcceptProfileIdentifier(
        string? value,
        out string profileId)
    {
        profileId = (value ?? string.Empty).Trim();
        if (profileId.Length is < 3 or > 128)
        {
            profileId = string.Empty;
            return false;
        }

        foreach (var ch in profileId)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '_' and not '-')
            {
                profileId = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static async Task<string?> ReadLimitedTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];

        while (memory.Length < maxBytes)
        {
            var remaining = maxBytes - (int)memory.Length;
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
                break;

            await memory.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
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

        if (!IsAuthorMetadataResponse(responseUrl))
            return false;

        var trimmed = responseBody.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return TryFindAuthorId(
                document.RootElement,
                IsGraphQlResponse(responseUrl),
                out authorId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindAuthorId(
        JsonElement element,
        bool profileContainersOnly,
        out string authorId)
    {
        authorId = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Profile-page GraphQL responses can also contain recommended authors.
            // Prefer profile-specific containers first and, for GraphQL, never treat
            // an arbitrary feed item's generic "author"/"user" as the monitored user.
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && IsPreferredProfileContainerName(property.Name)
                    && TryReadAuthorId(property.Value, out authorId))
                {
                    return true;
                }
            }

            if (!profileContainersOnly)
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
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAuthorId(
                        property.Value,
                        profileContainersOnly,
                        out authorId))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAuthorId(
                        item,
                        profileContainersOnly,
                        out authorId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAuthorContainerName(string name)
        => name.Equals("author", StringComparison.OrdinalIgnoreCase)
           || name.Equals("user", StringComparison.OrdinalIgnoreCase)
           || name.Equals("userinfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("userInfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("sensitiveUserInfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("userProfile", StringComparison.OrdinalIgnoreCase)
           || name.Equals("profileUser", StringComparison.OrdinalIgnoreCase)
           || name.Equals("profileInfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("visionProfile", StringComparison.OrdinalIgnoreCase)
           || name.Equals("owner", StringComparison.OrdinalIgnoreCase)
           || name.Equals("profile", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreferredProfileContainerName(string name)
        => name.Equals("sensitiveUserInfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("userProfile", StringComparison.OrdinalIgnoreCase)
           || name.Equals("profileUser", StringComparison.OrdinalIgnoreCase)
           || name.Equals("profileInfo", StringComparison.OrdinalIgnoreCase)
           || name.Equals("visionProfile", StringComparison.OrdinalIgnoreCase)
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

        if (!IsAuthorMetadataResponse(responseUrl))
            return false;

        var trimmed = responseBody.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return TryFindAuthorName(
                document.RootElement,
                IsGraphQlResponse(responseUrl),
                out authorName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindAuthorName(
        JsonElement element,
        bool profileContainersOnly,
        out string authorName)
    {
        authorName = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && IsPreferredProfileContainerName(property.Name)
                    && TryReadAuthorName(property.Value, out authorName))
                {
                    return true;
                }
            }

            if (!profileContainersOnly)
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
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAuthorName(
                        property.Value,
                        profileContainersOnly,
                        out authorName))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAuthorName(
                        item,
                        profileContainersOnly,
                        out authorName))
                {
                    return true;
                }
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

    private static bool IsGraphQlResponse(string responseUrl)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.Equals("/graphql", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/m_graphql", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/graphql/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthorMetadataResponse(string responseUrl)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (!host.EndsWith("kuaishou.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith("gifshow.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith("chenzhongtech.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var isGraphQl = IsGraphQlResponse(responseUrl);

        return path.Equals("/live_api/profile/public", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/rest/v/profile/feed", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/live_api/baseuser/userinfo/sensitive", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/live_api/profile/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/live_api/baseuser/", StringComparison.OrdinalIgnoreCase)
               || isGraphQl;
    }

    public bool TryParseAuthorAvatar(
        string responseUrl,
        string contentType,
        string responseBody,
        out string avatarUrl)
    {
        avatarUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Length > 2_000_000)
            return false;

        if (!IsAuthorMetadataResponse(responseUrl))
            return false;

        var trimmed = responseBody.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return TryFindAuthorAvatar(
                document.RootElement,
                IsGraphQlResponse(responseUrl),
                out avatarUrl);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindAuthorAvatar(
        JsonElement element,
        bool profileContainersOnly,
        out string avatarUrl)
    {
        avatarUrl = string.Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && IsPreferredProfileContainerName(property.Name)
                    && TryReadAuthorAvatar(property.Value, out avatarUrl))
                {
                    return true;
                }
            }

            if (!profileContainersOnly)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && IsAuthorContainerName(property.Name)
                        && TryReadAuthorAvatar(property.Value, out avatarUrl))
                    {
                        return true;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAuthorAvatar(
                        property.Value,
                        profileContainersOnly,
                        out avatarUrl))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAuthorAvatar(
                        item,
                        profileContainersOnly,
                        out avatarUrl))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadAuthorAvatar(JsonElement author, out string avatarUrl)
    {
        foreach (var propertyName in new[]
                 {
                     "headerUrl", "headerUrls", "avatar", "avatarUrl", "avatarUrls",
                     "profile", "head", "headUrl", "bigHead"
                 })
        {
            if (!TryGetPropertyIgnoreCase(author, propertyName, out var value))
                continue;

            if (TryReadFirstHttpUrl(value, out avatarUrl))
                return true;
        }

        avatarUrl = string.Empty;
        return false;
    }

    private static bool TryReadFirstHttpUrl(JsonElement element, out string url)
    {
        url = string.Empty;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString()?.Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    url = text!;
                    return true;
                }
                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryReadFirstHttpUrl(item, out url))
                        return true;
                }
                return false;

            case JsonValueKind.Object:
                foreach (var preferred in new[] { "url", "urls", "cdn", "src", "value" })
                {
                    if (TryGetPropertyIgnoreCase(element, preferred, out var child)
                        && TryReadFirstHttpUrl(child, out url))
                    {
                        return true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryReadFirstHttpUrl(property.Value, out url))
                        return true;
                }
                return false;

            default:
                return false;
        }
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
