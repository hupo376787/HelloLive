namespace HelloLive.Core.Utilities;

public static class AuthorFolderResolver
{
    public static string Resolve(
        string platformDownloadRoot,
        string? authorName,
        string? authorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformDownloadRoot);

        var preferred = Path.Combine(
            platformDownloadRoot,
            FileNameHelper.BuildAuthorFolderName(authorName, authorId));
        if (Directory.Exists(preferred))
            return preferred;

        var suffix = FileNameHelper.BuildAuthorFolderIdSuffix(authorId);
        if (string.IsNullOrWhiteSpace(suffix) || !Directory.Exists(platformDownloadRoot))
            return preferred;

        try
        {
            return Directory
                       .EnumerateDirectories(platformDownloadRoot, "*", SearchOption.TopDirectoryOnly)
                       .Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal))
                       .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                       .FirstOrDefault()
                   ?? preferred;
        }
        catch
        {
            return preferred;
        }
    }
}
