using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace HelloLive.Core.Controls;

/// <summary>
/// Text presenter that keeps normal text in Avalonia fonts and renders emoji as
/// Twemoji bitmaps in Browser/WASM, where system emoji fonts are not available.
/// Native Android/iOS/Desktop continue to use ordinary text rendering.
/// </summary>
public sealed class EmojiTextBlock : WrapPanel
{
    private const string TwemojiBaseUrl =
        "https://cdn.jsdelivr.net/gh/jdecked/twemoji@15.1.0/assets/72x72/";

    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> EmojiCache =
        new(StringComparer.OrdinalIgnoreCase);

    private int _generation;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<EmojiTextBlock, string>(
            nameof(Text),
            string.Empty);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<EmojiTextBlock, double>(
            nameof(FontSize),
            14d);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<EmojiTextBlock, FontWeight>(
            nameof(FontWeight),
            FontWeight.Normal);

    static EmojiTextBlock()
    {
        TextProperty.Changed.AddClassHandler<EmojiTextBlock>(
            static (control, _) => control.Rebuild());
        FontSizeProperty.Changed.AddClassHandler<EmojiTextBlock>(
            static (control, _) => control.Rebuild());
        FontWeightProperty.Changed.AddClassHandler<EmojiTextBlock>(
            static (control, _) => control.Rebuild());
    }

    public EmojiTextBlock()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    private void Rebuild()
    {
        var generation = ++_generation;
        Children.Clear();

        var text = Text ?? string.Empty;
        if (text.Length == 0)
            return;

        if (!OperatingSystem.IsBrowser())
        {
            Children.Add(CreateTextBlock(text));
            return;
        }

        var normalBuffer = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();

            if (!LooksLikeEmoji(element))
            {
                normalBuffer.Append(element);
                continue;
            }

            FlushNormalText(normalBuffer);

            var holder = new Border
            {
                Width = Math.Max(16d, FontSize + 3d),
                Height = Math.Max(16d, FontSize + 3d),
                VerticalAlignment = VerticalAlignment.Center,
                Child = CreateTextBlock(element)
            };

            Children.Add(holder);
            _ = LoadEmojiAsync(holder, element, generation);
        }

        FlushNormalText(normalBuffer);
    }

    private void FlushNormalText(StringBuilder buffer)
    {
        if (buffer.Length == 0)
            return;

        Children.Add(CreateTextBlock(buffer.ToString()));
        buffer.Clear();
    }

    private TextBlock CreateTextBlock(string text)
        => new()
        {
            Text = text,
            FontSize = FontSize,
            FontWeight = FontWeight,
            VerticalAlignment = VerticalAlignment.Center
        };

    private async Task LoadEmojiAsync(
        Border holder,
        string element,
        int generation)
    {
        var rawCode = ToTwemojiCode(element, keepVariationSelector: true);
        var normalizedCode = ToTwemojiCode(element, keepVariationSelector: false);

        var bitmap = await GetEmojiBitmapAsync(rawCode);
        if (bitmap is null
            && !string.Equals(rawCode, normalizedCode, StringComparison.OrdinalIgnoreCase))
        {
            bitmap = await GetEmojiBitmapAsync(normalizedCode);
        }

        if (bitmap is null || generation != _generation)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation)
                return;

            holder.Child = new Image
            {
                Source = bitmap,
                Width = Math.Max(16d, FontSize + 2d),
                Height = Math.Max(16d, FontSize + 2d),
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
        });
    }

    private static Task<Bitmap?> GetEmojiBitmapAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult<Bitmap?>(null);

        return EmojiCache.GetOrAdd(code, DownloadEmojiAsync);
    }

    private static async Task<Bitmap?> DownloadEmojiAsync(string code)
    {
        try
        {
            var bytes = await HttpClient.GetByteArrayAsync(
                $"{TwemojiBaseUrl}{code}.png");
            await using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static string ToTwemojiCode(
        string textElement,
        bool keepVariationSelector)
    {
        var parts = new List<string>();

        foreach (var rune in textElement.EnumerateRunes())
        {
            if (!keepVariationSelector && rune.Value == 0xFE0F)
                continue;

            parts.Add(rune.Value.ToString("x", CultureInfo.InvariantCulture));
        }

        return string.Join("-", parts);
    }

    private static bool LooksLikeEmoji(string textElement)
    {
        var hasEmojiBase = false;

        foreach (var rune in textElement.EnumerateRunes())
        {
            var value = rune.Value;

            if (value is >= 0x1F000 and <= 0x1FAFF
                || value is >= 0x2600 and <= 0x27BF
                || value is >= 0x2300 and <= 0x23FF
                || value is >= 0x2B00 and <= 0x2BFF
                || value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                hasEmojiBase = true;
            }
        }

        return hasEmojiBase;
    }
}
