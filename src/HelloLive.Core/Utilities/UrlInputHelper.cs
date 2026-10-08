using System.Text.RegularExpressions;

namespace HelloLive.Core.Utilities;

public static partial class UrlInputHelper
{
    /// <summary>
    /// Extracts the first HTTP/HTTPS URL from arbitrary share text.
    /// If the whole input is already a URL it is returned unchanged.
    /// Common Chinese/English trailing punctuation is removed.
    /// </summary>
    public static string ExtractFirstHttpUrl(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        if (Uri.TryCreate(text, UriKind.Absolute, out var direct)
            && (direct.Scheme == Uri.UriSchemeHttp
                || direct.Scheme == Uri.UriSchemeHttps))
        {
            return text;
        }

        var match = HttpUrlRegex().Match(text);
        if (!match.Success)
            return string.Empty;

        return TrimTrailingPunctuation(match.Value);
    }

    private static string TrimTrailingPunctuation(string value)
    {
        var end = value.Length;
        while (end > 0 && IsTrailingPunctuation(value[end - 1]))
            end--;

        return end == value.Length
            ? value
            : value[..end];
    }

    private static bool IsTrailingPunctuation(char value)
        => value is '.'
            or ','
            or ';'
            or ':'
            or '!'
            or '?'
            or ')'
            or ']'
            or '}'
            or '，'
            or '。'
            or '；'
            or '：'
            or '！'
            or '？'
            or '）'
            or '】'
            or '》'
            or '」'
            or '』';

    [GeneratedRegex(
        @"https?://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRegex();
}
