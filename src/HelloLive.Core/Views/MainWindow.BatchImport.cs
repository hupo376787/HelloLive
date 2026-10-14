using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using HelloLive.Core.ViewModels;

namespace HelloLive.Core.Views;

public partial class MainWindow
{
    private async void BatchImportButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var content = await ShowBatchImportDialogAsync();
        if (content is null)
            return;

        await viewModel.BatchImportMonitorsAsync(content);
    }

    private async Task<string?> ShowBatchImportDialogAsync()
    {
        var isDark = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;

        var textPrimary = new SolidColorBrush(
            Color.Parse(isDark ? "#F7F7FB" : "#202231"));
        var textSecondary = new SolidColorBrush(
            Color.Parse(isDark ? "#C3C7D4" : "#62687A"));
        var frameBackground = new SolidColorBrush(
            Color.Parse(isDark ? "#FF1B1927" : "#FFF8F7FC"));
        var contentBackground = new SolidColorBrush(
            Color.Parse(isDark ? "#FF232131" : "#FFF8F7FC"));
        var borderBrush = new SolidColorBrush(
            Color.Parse(isDark ? "#55FFFFFF" : "#331B1D2A"));
        var titleTextBrush = new SolidColorBrush(
            Color.Parse(isDark ? "#FFF7F7FB" : "#FF4A355E"));
        var titleHintBrush = new SolidColorBrush(
            Color.Parse(isDark ? "#D9FFFFFF" : "#B85B4770"));

        var titleBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative)
        };

        if (isDark)
        {
            titleBrush.GradientStops.Add(
                new GradientStop(Color.Parse("#B55B21B6"), 0));
            titleBrush.GradientStops.Add(
                new GradientStop(Color.Parse("#B9BE185D"), 0.52));
            titleBrush.GradientStops.Add(
                new GradientStop(Color.Parse("#AD0369A1"), 1));
        }
        else
        {
            titleBrush.GradientStops.Add(
                new GradientStop(Color.Parse("#F3DECDF4"), 0));
            titleBrush.GradientStops.Add(
                new GradientStop(Color.Parse("#F3F0D3E5"), 0.52));
            titleBrush.GradientStops.Add(
                new GradientStop(Color.Parse("#F3D5E6F4"), 1));
        }

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText =
                "每行一个作者主页、直播间地址、短链或分享文本。程序会自动提取每行中的链接。",
            MinHeight = 300,
            Padding = new Thickness(14),
            Background = Brushes.Transparent,
            Foreground = textPrimary,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        editor.Resources["TextControlBackground"] = Brushes.Transparent;
        editor.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        editor.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        editor.Resources["TextControlBackgroundDisabled"] = Brushes.Transparent;

        ScrollViewer.SetHorizontalScrollBarVisibility(
            editor,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(
            editor,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var title = new TextBlock
        {
            Text = "批量导入作者",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = titleTextBrush
        };

        var titleHint = new TextBlock
        {
            Text = "批量加入监控列表",
            FontSize = 11,
            Foreground = titleHintBrush,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var titlePanel = new StackPanel
        {
            Margin = new Thickness(18, 0, 0, 0),
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                title,
                titleHint
            }
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 44,
            Height = 40,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0),
            FontSize = 22,
            Background = Brushes.Transparent,
            Foreground = titleTextBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        Window? dialog = null;

        var dragRegion = new Border
        {
            Background = Brushes.Transparent,
            Child = titlePanel
        };
        dragRegion.PointerPressed += (_, pointerEvent) =>
        {
            if (dialog is null)
                return;

            var point = pointerEvent.GetCurrentPoint(dragRegion);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            dialog.BeginMoveDrag(pointerEvent);
            pointerEvent.Handled = true;
        };

        var titleBarGrid = new Grid();
        titleBarGrid.ColumnDefinitions.Add(
            new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        titleBarGrid.ColumnDefinitions.Add(
            new ColumnDefinition(GridLength.Auto));
        titleBarGrid.Children.Add(dragRegion);
        Grid.SetColumn(closeButton, 1);
        titleBarGrid.Children.Add(closeButton);

        var titleBar = new Border
        {
            Height = 66,
            Background = titleBrush,
            Child = titleBarGrid
        };

        var description = new TextBlock
        {
            Text =
                "每行输入一个作者主页地址。也可以直接粘贴包含链接的分享文本；HelloLive 会自动识别平台、解析短链并按顺序加入监控列表。空行和重复作者会自动跳过。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = textSecondary
        };

        var importButton = new Button
        {
            Content = "导入 TXT",
            MinWidth = 132,
            Height = 42,
            Padding = new Thickness(14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(
                Color.Parse(isDark ? "#554A465D" : "#66FFFFFF")),
            Foreground = textPrimary,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };

        var confirmButton = new Button
        {
            Content = "开始导入",
            MinWidth = 112,
            Height = 42,
            Padding = new Thickness(14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#FD6F71")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            FontWeight = FontWeight.SemiBold
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Children =
            {
                importButton,
                confirmButton
            }
        };

        var contentGrid = new Grid
        {
            RowSpacing = 16
        };
        contentGrid.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        contentGrid.RowDefinitions.Add(
            new RowDefinition(new GridLength(1, GridUnitType.Star)));
        contentGrid.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        contentGrid.Children.Add(description);
        Grid.SetRow(editor, 1);
        contentGrid.Children.Add(editor);
        Grid.SetRow(buttons, 2);
        contentGrid.Children.Add(buttons);

        var contentBorder = new Border
        {
            Padding = new Thickness(24),
            Background = contentBackground,
            Child = contentGrid
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(
            new RowDefinition(GridLength.Auto));
        rootGrid.RowDefinitions.Add(
            new RowDefinition(new GridLength(1, GridUnitType.Star)));
        rootGrid.Children.Add(titleBar);
        Grid.SetRow(contentBorder, 1);
        rootGrid.Children.Add(contentBorder);

        var frame = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Background = frameBackground,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            Child = rootGrid
        };

        dialog = new Window
        {
            Title = "批量导入作者",
            Width = 900,
            Height = 580,
            MinWidth = 620,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            Background = frameBackground,
            TransparencyBackgroundFallback = frameBackground,
            Content = frame
        };

        dialog.Opened += (_, _) => editor.Focus();
        dialog.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key != Key.Escape)
                return;

            keyEvent.Handled = true;
            dialog.Close((string?)null);
        };

        closeButton.Click += (_, _) =>
            dialog.Close((string?)null);

        confirmButton.Click += (_, _) =>
        {
            var value = editor.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return;

            dialog.Close(value);
        };

        importButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "选择批量作者地址文本文件",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("文本文件")
                        {
                            Patterns = new[] { "*.txt" },
                            MimeTypes = new[] { "text/plain" }
                        }
                    }
                });

            if (files.Count == 0)
                return;

            importButton.IsEnabled = false;
            confirmButton.IsEnabled = false;

            try
            {
                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(
                    stream,
                    detectEncodingFromByteOrderMarks: true);

                var importedContent = await reader.ReadToEndAsync();

                editor.Text = string.IsNullOrWhiteSpace(editor.Text)
                    ? importedContent
                    : editor.Text.TrimEnd()
                      + Environment.NewLine
                      + importedContent;

                editor.CaretIndex = editor.Text?.Length ?? 0;
                editor.Focus();
            }
            catch (Exception ex)
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    viewModel.AddRemoteLog(
                        $"读取批量作者 TXT 失败：{ex.Message}");
                }
            }
            finally
            {
                importButton.IsEnabled = true;
                confirmButton.IsEnabled = true;
            }
        };

        return await dialog.ShowDialog<string?>(this);
    }
}
