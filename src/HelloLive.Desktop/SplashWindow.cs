using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using HelloLive.Core.Controls;

namespace HelloLive.Desktop;

/// <summary>
/// Desktop startup splash. It stays lightweight and only reflects startup progress.
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly TextBlock _detailText;
    private readonly TextBlock _percentText;
    private readonly Button _closeButton;

    public SplashWindow()
    {
        Title = "HelloLive";
        Width = 560;
        Height = 330;
        MinWidth = 560;
        MinHeight = 330;
        MaxWidth = 560;
        MaxHeight = 330;
        CanResize = false;
        ShowInTaskbar = true;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#FFF4F6FC"));

        TryApplyWindowIcon();

        var logoHost = new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.Parse("#14FD7893")),
            Padding = new Thickness(4),
            Child = new LiveLogo()
        };

        var title = new TextBlock
        {
            Text = "HelloLive",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF172033"))
        };

        var subtitle = new TextBlock
        {
            Text = "正在准备应用，请稍候",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#FF7A8498"))
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                logoHost,
                new StackPanel
                {
                    Spacing = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { title, subtitle }
                }
            }
        };

        _statusText = new TextBlock
        {
            Text = "正在启动 HelloLive…",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF26324A")),
            VerticalAlignment = VerticalAlignment.Center
        };

        _percentText = new TextBlock
        {
            Text = "0%",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF7C3AED")),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(
            new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        statusGrid.Children.Add(_statusText);
        Grid.SetColumn(_percentText, 1);
        statusGrid.Children.Add(_percentText);

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(Color.Parse("#FF7C3AED")),
            Background = new SolidColorBrush(Color.Parse("#FFE3E6F0"))
        };

        _detailText = new TextBlock
        {
            Text = "准备应用运行环境",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#FF7A8498")),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 18
        };

        _closeButton = new Button
        {
            Content = "关闭",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(20, 7),
            Margin = new Thickness(0, 4, 0, 0)
        };
        _closeButton.Click += (_, _) => Close();

        Content = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(34, 30),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.Parse("#FFF9FAFE")),
            BorderBrush = new SolidColorBrush(Color.Parse("#337C3AED")),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    header,
                    new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 8, 0, 30),
                        Background = new SolidColorBrush(Color.Parse("#FFE3E6F0"))
                    },
                    statusGrid,
                    _progressBar,
                    _detailText,
                    _closeButton
                }
            }
        };
    }

    public async Task WaitUntilPresentedAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(80);
    }

    public void SetProgress(double percent, string status, string? detail = null)
    {
        var value = Math.Clamp(percent, 0d, 100d);
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = value;
        _percentText.Text = $"{value:0}%";
        _statusText.Text = status;
        _detailText.Text = detail ?? string.Empty;
    }

    public void ShowFailure(string message)
    {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _progressBar.Foreground = new SolidColorBrush(Color.Parse("#FFDC2626"));
        _percentText.Text = "!";
        _percentText.Foreground = new SolidColorBrush(Color.Parse("#FFDC2626"));
        _statusText.Text = "启动失败";
        _statusText.Foreground = new SolidColorBrush(Color.Parse("#FFB91C1C"));
        _detailText.Text = string.IsNullOrWhiteSpace(message)
            ? "初始化过程中发生未知错误。"
            : message;
        _detailText.Foreground = new SolidColorBrush(Color.Parse("#FFB91C1C"));
        _closeButton.IsVisible = true;
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://HelloLive/Assets/app-icon.ico"));
            Icon = new WindowIcon(stream);
        }
        catch
        {
            // Splash icon failure must never block startup.
        }
    }
}
