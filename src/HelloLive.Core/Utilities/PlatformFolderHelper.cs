using System.Globalization;

namespace HelloLive.Core.Utilities;

public static class PlatformFolderHelper
{
    public static string GetFolderName(string? platform)
    {
        var value = platform?.Trim() ?? string.Empty;

        if (value.Equals("kuaishou", StringComparison.OrdinalIgnoreCase)
            || value.Equals("kuaishou-live", StringComparison.OrdinalIgnoreCase)
            || value.Contains("快手", StringComparison.Ordinal))
        {
            return "kuaishou";
        }

        if (value.Equals("douyin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("抖音", StringComparison.Ordinal))
        {
            return "douyin";
        }

        if (string.IsNullOrWhiteSpace(value))
            return "other";

        return FileNameHelper.Sanitize(
            value.ToLower(CultureInfo.InvariantCulture),
            50);
    }
}
