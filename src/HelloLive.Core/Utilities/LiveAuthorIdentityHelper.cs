namespace HelloLive.Core.Utilities;

public static class LiveAuthorIdentityHelper
{
    /// <summary>
    /// Returns a real/stable author id only when it is safe to infer it from the URL.
    /// Kuaishou /u/, /profile/ and /fw/live/ values are page/principal/live identifiers,
    /// not the account author.id used by HelloCrab download folders, so they are deliberately
    /// NOT returned as an author id.
    /// </summary>
    public static string ExtractStableAuthorId(string? profileUrl, string? resolvedPageUrl = null)
    {
        var resolved = ExtractStableFromUrl(resolvedPageUrl);
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        return ExtractStableFromUrl(profileUrl);
    }

    public static string ExtractPageIdentifier(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return string.Empty;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return string.Empty;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("u", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("profile", StringComparison.OrdinalIgnoreCase))
            {
                return FileNameHelper.Sanitize(
                    Uri.UnescapeDataString(segments[i + 1]),
                    64);
            }

            if (segments[i].Equals("live", StringComparison.OrdinalIgnoreCase)
                && i > 0
                && segments[i - 1].Equals("fw", StringComparison.OrdinalIgnoreCase))
            {
                return FileNameHelper.Sanitize(
                    Uri.UnescapeDataString(segments[i + 1]),
                    64);
            }
        }

        return FileNameHelper.Sanitize(
            Uri.UnescapeDataString(segments[^1]),
            64);
    }

    public static bool IsKuaishouPageIdentifier(string? profileUrl, string? candidateAuthorId)
    {
        if (string.IsNullOrWhiteSpace(candidateAuthorId)
            || !Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri)
            || !IsKuaishouHost(uri.Host))
        {
            return false;
        }

        var pageId = ExtractPageIdentifier(profileUrl);
        return !string.IsNullOrWhiteSpace(pageId)
               && string.Equals(
                   pageId,
                   candidateAuthorId.Trim(),
                   StringComparison.Ordinal);
    }

    private static string ExtractStableFromUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return string.Empty;

        // Kuaishou's visible profile/live URL id is a principal/live page identifier.
        // Douyin's live web_rid and /user/{sec_uid} are also not the numeric account id
        // used by the download folder. Both platforms therefore wait for API metadata.
        if (IsKuaishouHost(uri.Host) || IsDouyinHost(uri.Host))
            return string.Empty;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return string.Empty;

        return FileNameHelper.Sanitize(
            Uri.UnescapeDataString(segments[^1]),
            64);
    }

    private static bool IsKuaishouHost(string host)
        => host.Equals("live.kuaishou.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("www.kuaishou.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("kuaishou.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("v.kuaishou.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".kuaishou.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".chenzhongtech.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".gifshow.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDouyinHost(string host)
        => host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase);
}
