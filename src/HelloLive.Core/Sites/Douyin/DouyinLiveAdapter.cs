using HelloLive.Core.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelloLive.Core.Sites.Douyin;

/// <summary>
/// 抖音直播适配器。
/// 以 live.douyin.com 的 room/web/enter 响应为主数据源，优先选择 H.264 HTTP-FLV，
/// 同时保留真实媒体请求和 HLS 的兼容兜底。
/// </summary>
public sealed partial class DouyinLiveAdapter : ILivePlatformAdapter
{
    private static readonly string[] SupportedHostSuffixes =
    [
        "douyin.com",
        "iesdouyin.com"
    ];

    private static readonly string[] StreamHostSuffixes =
    [
        ".douyincdn.com",
        ".smtcdns.net",
        ".bytecdn.cn",
        ".bytecdn.com"
    ];

    private static readonly HttpClient RedirectClient = CreateRedirectClient();

    public string Id => "douyin";
    public string DisplayName => "抖音";
    public string HomeUrl => "https://live.douyin.com/";

    public bool CanHandleProfileUrl(string url)
    {
        if (!TryCreateUri(url, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return SupportedHostSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase));
    }

    public string NormalizeProfileUrl(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0)
            return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = "https://" + value.TrimStart('/');
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
                return value;
        }

        if (TryExtractLiveWebRid(uri, out var webRid))
            return BuildCanonicalLiveUrl(webRid);

        return uri.AbsoluteUri;
    }

    public async Task<string> ResolveProfileUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProfileUrl(url);
        if (!TryCreateUri(normalized, out var originalUri))
            return normalized;

        if (TryExtractLiveWebRid(originalUri, out var directWebRid))
            return BuildCanonicalLiveUrl(directWebRid);

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
            if (TryExtractLiveWebRid(resolvedUri, out var redirectedWebRid))
                return BuildCanonicalLiveUrl(redirectedWebRid);

            responseBody = await ReadLimitedTextAsync(
                response.Content,
                768 * 1024,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 分享短链展开是 best-effort；浏览器后续仍可继续处理客户端跳转。
        }

        if (TryExtractLiveWebRidFromBody(responseBody, out var bodyWebRid))
            return BuildCanonicalLiveUrl(bodyWebRid);

        return resolvedUri.AbsoluteUri;
    }

    public bool TryParseStreamRequest(
        string requestUrl,
        string resourceType,
        out LiveStreamInfo stream)
    {
        stream = null!;
        if (!TryCreateUri(requestUrl, out var uri)
            || !IsKnownStreamHost(uri.Host))
        {
            return false;
        }

        var format = GetStreamFormat(uri.AbsolutePath);
        if (format is null)
            return false;

        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("only_audio", out var onlyAudio)
            && onlyAudio == "1")
        {
            return false;
        }

        // 当前抖音桌面页可能会先发起 H.265 FLV 请求，随后 room/web/enter
        // 才返回 H.264/H.265 完整档位。这里故意不把 H.265 的首个媒体请求
        // 当成最终结果，等待 API 中兼容性更好的 H.264 FLV。
        if (TryReadQueryValue(query, "biz_vcodec", "codec") is { } codec
            && IsH265Codec(codec))
        {
            return false;
        }

        stream = new LiveStreamInfo(
            requestUrl,
            format,
            GuessQuality(uri, query),
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
        if (!IsRoomEnterResponse(responseUrl)
            || string.IsNullOrWhiteSpace(responseBody)
            || responseBody.Length > 2_000_000)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!TryGetLiveRoom(document.RootElement, out var room))
                return false;

            return TryReadBestRoomStream(room, out stream);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool TryParseAuthorId(
        string responseUrl,
        string contentType,
        string responseBody,
        out string authorId)
    {
        authorId = string.Empty;
        if (!TryParseRoomEnterRoot(responseUrl, responseBody, out var document))
            return false;

        using (document)
        {
            if (!TryGetRoomEnterData(document.RootElement, out var data))
                return false;

            if (TryGetObject(data, "user", out var user)
                && TryReadAuthorId(user, out authorId))
            {
                return true;
            }

            return TryGetFirstRoom(data, out var room)
                   && TryGetObject(room, "owner", out var owner)
                   && TryReadAuthorId(owner, out authorId);
        }
    }

    public bool TryParseAuthorName(
        string responseUrl,
        string contentType,
        string responseBody,
        out string authorName)
    {
        authorName = string.Empty;
        if (!TryParseRoomEnterRoot(responseUrl, responseBody, out var document))
            return false;

        using (document)
        {
            if (!TryGetRoomEnterData(document.RootElement, out var data))
                return false;

            if (TryGetObject(data, "user", out var user)
                && TryReadNonEmptyString(user, "nickname", out authorName))
            {
                return true;
            }

            return TryGetFirstRoom(data, out var room)
                   && TryGetObject(room, "owner", out var owner)
                   && TryReadNonEmptyString(owner, "nickname", out authorName);
        }
    }

    public bool TryParseAuthorAvatar(
        string responseUrl,
        string contentType,
        string responseBody,
        out string avatarUrl)
    {
        avatarUrl = string.Empty;
        if (!TryParseRoomEnterRoot(responseUrl, responseBody, out var document))
            return false;

        using (document)
        {
            if (!TryGetRoomEnterData(document.RootElement, out var data))
                return false;

            if (TryGetObject(data, "user", out var user)
                && TryReadAvatarUrl(user, out avatarUrl))
            {
                return true;
            }

            return TryGetFirstRoom(data, out var room)
                   && TryGetObject(room, "owner", out var owner)
                   && TryReadAvatarUrl(owner, out avatarUrl);
        }
    }

    private static bool TryReadBestRoomStream(
        JsonElement room,
        out LiveStreamInfo stream)
    {
        stream = null!;
        if (!TryGetObject(room, "stream_url", out var streamUrl))
            return false;

        if (TryGetObject(streamUrl, "live_core_sdk_data", out var liveCore)
            && TryGetObject(liveCore, "pull_data", out var pullData)
            && TryReadPullDataStream(pullData, out stream))
        {
            return true;
        }

        // 部分响应把同结构的 stream_data 放在 pull_datas。
        if (TryGetObject(streamUrl, "pull_datas", out var pullDatas))
        {
            foreach (var property in pullDatas.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && TryReadPullDataStream(property.Value, out stream))
                {
                    return true;
                }
            }
        }

        // 当前 Web API 的稳定兜底。优先 FLV，让 HelloLive 无需 FFmpeg 即可录像。
        if (TryReadUrlMap(streamUrl, "flv_pull_url", "HTTP-FLV", out stream))
            return true;

        return TryReadUrlMap(streamUrl, "hls_pull_url_map", "HLS", out stream);
    }

    private static bool TryReadPullDataStream(
        JsonElement pullData,
        out LiveStreamInfo stream)
    {
        stream = null!;
        if (!TryReadNonEmptyString(pullData, "stream_data", out var streamDataJson))
            return false;

        try
        {
            using var streamDataDocument = JsonDocument.Parse(streamDataJson);
            if (!TryGetObject(streamDataDocument.RootElement, "data", out var data))
                return false;

            var defaultKey = TryReadDefaultQualityKey(pullData);
            var qualityNames = ReadQualityNames(pullData);
            var candidates = new List<StreamCandidate>();

            foreach (var property in data.EnumerateObject())
            {
                var key = property.Name;
                if (key.Equals("ao", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind != JsonValueKind.Object
                    || !TryGetObject(property.Value, "main", out var main))
                {
                    continue;
                }

                var codec = ReadCodec(main);
                if (IsH265Codec(codec))
                    continue;

                var bitrate = ReadBitrate(main);
                var area = ReadResolutionArea(main);
                var quality = qualityNames.TryGetValue(key, out var mappedName)
                    ? mappedName
                    : key;
                var isDefault = !string.IsNullOrWhiteSpace(defaultKey)
                                && key.Equals(defaultKey, StringComparison.OrdinalIgnoreCase);

                var flv = ReadAbsoluteHttpUrl(main, "flv");
                if (!string.IsNullOrWhiteSpace(flv))
                {
                    candidates.Add(new StreamCandidate(
                        flv,
                        "HTTP-FLV",
                        quality,
                        isDefault,
                        bitrate,
                        area));
                }

                var hls = ReadAbsoluteHttpUrl(main, "hls");
                if (!string.IsNullOrWhiteSpace(hls))
                {
                    candidates.Add(new StreamCandidate(
                        hls,
                        "HLS",
                        quality,
                        isDefault,
                        bitrate,
                        area));
                }
            }

            var best = candidates
                .OrderByDescending(x =>
                    x.Format.Equals("HTTP-FLV", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.IsDefault)
                .ThenByDescending(x => x.Bitrate)
                .ThenByDescending(x => x.PixelArea)
                .FirstOrDefault();

            if (best is null)
                return false;

            stream = new LiveStreamInfo(
                best.Url,
                best.Format,
                best.Quality,
                "douyin-room-api");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadUrlMap(
        JsonElement streamUrl,
        string propertyName,
        string format,
        out LiveStreamInfo stream)
    {
        stream = null!;
        if (!TryGetObject(streamUrl, propertyName, out var map))
            return false;

        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var url = NormalizeMediaUrl(property.Value.GetString());
            if (url is null)
                continue;

            stream = new LiveStreamInfo(
                url,
                format,
                property.Name,
                "douyin-room-api-fallback");
            return true;
        }

        return false;
    }

    private static string? TryReadDefaultQualityKey(JsonElement pullData)
    {
        if (!TryGetObject(pullData, "options", out var options)
            || !TryGetObject(options, "default_quality", out var quality))
        {
            return null;
        }

        return ReadString(quality, "sdk_key")?.Trim();
    }

    private static Dictionary<string, string> ReadQualityNames(JsonElement pullData)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetObject(pullData, "options", out var options))
            return result;

        if (TryGetObject(options, "default_quality", out var defaultQuality))
            AddQualityName(defaultQuality, result);

        if (TryGetArray(options, "qualities", out var qualities))
        {
            foreach (var quality in qualities.EnumerateArray())
                AddQualityName(quality, result);
        }

        return result;
    }

    private static void AddQualityName(
        JsonElement quality,
        IDictionary<string, string> result)
    {
        var key = ReadString(quality, "sdk_key")?.Trim();
        var name = ReadString(quality, "name")?.Trim();
        if (!string.IsNullOrWhiteSpace(key)
            && !string.IsNullOrWhiteSpace(name))
        {
            result[key] = name;
        }
    }

    private static string ReadCodec(JsonElement main)
    {
        if (!TryReadNonEmptyString(main, "sdk_params", out var sdkParams))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(sdkParams);
            return ReadString(document.RootElement, "VCodec")
                   ?? ReadString(document.RootElement, "vcodec")
                   ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static long ReadBitrate(JsonElement main)
    {
        if (!TryReadNonEmptyString(main, "sdk_params", out var sdkParams))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(sdkParams);
            return ReadInt64(document.RootElement, "vbitrate");
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static long ReadResolutionArea(JsonElement main)
    {
        if (!TryReadNonEmptyString(main, "sdk_params", out var sdkParams))
            return 0;

        try
        {
            using var document = JsonDocument.Parse(sdkParams);
            var value = ReadString(document.RootElement, "resolution");
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            var parts = value.Split('x', 'X');
            return parts.Length == 2
                   && long.TryParse(parts[0], out var width)
                   && long.TryParse(parts[1], out var height)
                ? width * height
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? ReadAbsoluteHttpUrl(
        JsonElement parent,
        string propertyName)
    {
        var value = ReadString(parent, propertyName);
        return NormalizeMediaUrl(value);
    }

    private static string? NormalizeMediaUrl(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value)?.Trim();
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static bool TryParseRoomEnterRoot(
        string responseUrl,
        string responseBody,
        out JsonDocument document)
    {
        document = null!;
        if (!IsRoomEnterResponse(responseUrl)
            || string.IsNullOrWhiteSpace(responseBody)
            || responseBody.Length > 2_000_000)
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(responseBody);
            return true;
        }
        catch (JsonException)
        {
            document?.Dispose();
            document = null!;
            return false;
        }
    }

    private static bool TryGetLiveRoom(
        JsonElement root,
        out JsonElement room)
    {
        room = default;
        if (ReadInt64(root, "status_code") != 0
            || !TryGetRoomEnterData(root, out var data)
            || !TryGetFirstRoom(data, out room))
        {
            return false;
        }

        return ReadInt64(room, "status") == 2;
    }

    private static bool TryGetRoomEnterData(
        JsonElement root,
        out JsonElement data)
        => TryGetObject(root, "data", out data);

    private static bool TryGetFirstRoom(
        JsonElement data,
        out JsonElement room)
    {
        room = default;
        if (!TryGetArray(data, "data", out var rooms))
            return false;

        foreach (var item in rooms.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                room = item;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadAuthorId(
        JsonElement user,
        out string authorId)
    {
        foreach (var propertyName in new[]
                 {
                     "id_str", "uid", "id", "open_id_str", "sec_uid"
                 })
        {
            if (TryReadNonEmptyString(user, propertyName, out authorId))
                return true;
        }

        authorId = string.Empty;
        return false;
    }

    private static bool TryReadAvatarUrl(
        JsonElement user,
        out string avatarUrl)
    {
        foreach (var propertyName in new[]
                 {
                     "avatar_larger",
                     "avatar_300x300",
                     "avatar_medium",
                     "avatar_thumb"
                 })
        {
            if (!TryGetObject(user, propertyName, out var container))
                continue;

            if (TryGetArray(container, "url_list", out var urls))
            {
                foreach (var item in urls.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;

                    var value = NormalizeMediaUrl(item.GetString());
                    if (value is null)
                        continue;

                    avatarUrl = value;
                    return true;
                }
            }

            var direct = NormalizeMediaUrl(ReadString(container, "url"));
            if (direct is not null)
            {
                avatarUrl = direct;
                return true;
            }
        }

        avatarUrl = string.Empty;
        return false;
    }

    private static bool IsRoomEnterResponse(string responseUrl)
    {
        if (!TryCreateUri(responseUrl, out var uri))
            return false;

        return uri.Host.Equals("live.douyin.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsolutePath.TrimEnd('/').Equals(
                   "/webcast/room/web/enter",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractLiveWebRid(Uri uri, out string webRid)
    {
        webRid = string.Empty;
        if (!uri.Host.Equals("live.douyin.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 1)
            return false;

        return TryAcceptWebRid(segments[0], out webRid);
    }

    private static bool TryExtractLiveWebRidFromBody(
        string? body,
        out string webRid)
    {
        webRid = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var normalized = WebUtility.HtmlDecode(body)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        var match = LiveUrlRegex().Match(normalized);
        return match.Success
               && TryAcceptWebRid(match.Groups[1].Value, out webRid);
    }

    private static bool TryAcceptWebRid(
        string? value,
        out string webRid)
    {
        webRid = (value ?? string.Empty).Trim();
        if (webRid.Length is < 5 or > 32
            || !webRid.All(char.IsDigit))
        {
            webRid = string.Empty;
            return false;
        }

        return true;
    }

    private static string BuildCanonicalLiveUrl(string webRid)
        => $"https://live.douyin.com/{webRid}";

    private static bool IsKnownStreamHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (host.Contains("pull-flv", StringComparison.OrdinalIgnoreCase)
            || host.Contains("pull-hls", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return StreamHostSuffixes.Any(suffix =>
            host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetStreamFormat(string absolutePath)
    {
        var path = absolutePath.ToLowerInvariant();
        if (path.EndsWith(".flv", StringComparison.Ordinal))
            return "HTTP-FLV";
        if (path.EndsWith(".m3u8", StringComparison.Ordinal))
            return "HLS";
        return null;
    }

    private static string? GuessQuality(
        Uri uri,
        IReadOnlyDictionary<string, string> query)
    {
        if (query.TryGetValue("biz_quality", out var quality)
            && !string.IsNullOrWhiteSpace(quality))
        {
            return quality;
        }

        var file = Path.GetFileNameWithoutExtension(uri.AbsolutePath).ToLowerInvariant();
        foreach (var candidate in new[] { "origin", "or4", "hd", "sd", "ld", "md" })
        {
            if (file.EndsWith('_' + candidate, StringComparison.Ordinal)
                || file.EndsWith(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsH265Codec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return false;

        var value = codec.Trim().ToLowerInvariant();
        return value.Contains("265", StringComparison.Ordinal)
               || value.Contains("hevc", StringComparison.Ordinal)
               || value.Contains("bytevc1", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator >= 0 ? pair[..separator] : pair;
            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            name = WebUtility.UrlDecode(name);
            value = WebUtility.UrlDecode(value);
            if (!string.IsNullOrWhiteSpace(name))
                result[name] = value;
        }

        return result;
    }

    private static string? TryReadQueryValue(
        IReadOnlyDictionary<string, string> query,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (query.TryGetValue(name, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryCreateUri(string? value, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(value)
               && Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
               && uri.Scheme is "http" or "https";
    }

    private static bool TryReadNonEmptyString(
        JsonElement parent,
        string propertyName,
        out string value)
    {
        value = ReadString(parent, propertyName)?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ReadString(
        JsonElement parent,
        string propertyName)
    {
        if (!TryGetProperty(parent, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long ReadInt64(
        JsonElement parent,
        string propertyName)
    {
        if (!TryGetProperty(parent, propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
               && long.TryParse(value.GetString(), out number)
            ? number
            : 0;
    }

    private static bool TryGetObject(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
        => TryGetProperty(parent, propertyName, out value)
           && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
        => TryGetProperty(parent, propertyName, out value)
           && value.ValueKind == JsonValueKind.Array;

    private static bool TryGetProperty(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
            return false;

        if (parent.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
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
            "Chrome/149.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
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

    private sealed record StreamCandidate(
        string Url,
        string Format,
        string Quality,
        bool IsDefault,
        long Bitrate,
        long PixelArea);

    [GeneratedRegex(
        @"https?://live\.douyin\.com/(\d{5,32})(?:[/?#]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveUrlRegex();
}
