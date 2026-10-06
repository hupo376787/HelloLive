namespace HelloLive.Core.Utilities;

public static class LiveAuthorIdentityHelper
{
    public static string ExtractStableAuthorId(string? profileUrl, string? resolvedPageUrl = null)
    {
        var resolved = ExtractFromUrl(resolvedPageUrl);
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        return ExtractFromUrl(profileUrl);
    }

    private static string ExtractFromUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return string.Empty;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
            return string.Empty;

        // 快手桌面作者主页：https://live.kuaishou.com/u/{authorId}
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("u", StringComparison.OrdinalIgnoreCase))
                return FileNameHelper.Sanitize(Uri.UnescapeDataString(segments[i + 1]), 64);

            // 分享短链最终常落到 /fw/live/{id}。
            if (segments[i].Equals("live", StringComparison.OrdinalIgnoreCase))
                return FileNameHelper.Sanitize(Uri.UnescapeDataString(segments[i + 1]), 64);
        }

        // 普通平台主页先使用末段作为稳定 ID；后续平台适配器解析出更准确 ID 时可覆盖。
        var last = Uri.UnescapeDataString(segments[^1]);
        return FileNameHelper.Sanitize(last, 64);
    }
}
