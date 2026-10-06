using System.Globalization;
using System.Text;

namespace HelloLive.Core.Utilities;

public static class FileNameHelper
{
    private static readonly HashSet<char> InvalidChars = Path.GetInvalidFileNameChars().ToHashSet();

    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
            return "无标题";

        var normalized = value.Normalize();
        var builder = new StringBuilder(Math.Min(normalized.Length, maxLength));
        var previousWasSpace = false;
        var elements = StringInfo.GetTextElementEnumerator(normalized);

        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var containsInvalidCharacter = element.Any(ch => InvalidChars.Contains(ch) || char.IsControl(ch));
            var isWhitespace = !containsInvalidCharacter && element.All(char.IsWhiteSpace);
            var mapped = containsInvalidCharacter ? "_" : isWhitespace ? " " : element;

            if (isWhitespace)
            {
                if (previousWasSpace)
                    continue;
                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            if (builder.Length + mapped.Length > maxLength)
                break;

            builder.Append(mapped);
        }

        var result = builder.ToString().Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(result) ? "无标题" : result;
    }

    public static string BuildAuthorFolderName(string? authorName, string? authorId, int maxLength = 120)
    {
        var idSuffix = BuildAuthorFolderIdSuffix(authorId, maxLength);
        if (string.IsNullOrWhiteSpace(idSuffix))
            return Sanitize(authorName, maxLength);

        var nameLength = Math.Max(1, maxLength - idSuffix.Length);
        var safeName = Sanitize(authorName, nameLength);
        return Sanitize(safeName + idSuffix, maxLength);
    }

    public static string BuildAuthorFolderIdSuffix(string? authorId, int maxLength = 120)
    {
        var safeId = string.IsNullOrWhiteSpace(authorId)
            ? string.Empty
            : Sanitize(authorId, Math.Min(64, Math.Max(1, maxLength - 3)));
        return string.IsNullOrWhiteSpace(safeId) ? string.Empty : $"({safeId})";
    }
}
