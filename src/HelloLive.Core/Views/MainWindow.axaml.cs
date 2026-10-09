using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HelloLive.Core.Models;
using HelloLive.Core.ViewModels;
using System.Diagnostics;

namespace HelloLive.Core.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _shutdownInProgress;
    private LiveMonitorTarget? _editingUrlTarget;

    private const double ResizeBorderThickness = 7;

    public event EventHandler? MinimizeToTrayRequested;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;

        // The window uses WindowDecorations=None, so the native resize border no
        // longer supplies hit testing/cursors for us. Handle the outer 7 px here
        // and delegate the actual resize operation back to Avalonia.
        AddHandler(
            PointerMovedEvent,
            Window_PointerMovedForResize,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerPressedEvent,
            Window_PointerPressedForResize,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PointerExited += Window_PointerExitedForResize;
    }

    private void MainWindow_Closing(
        object? sender,
        WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        ShowCloseConfirmation();
    }

    private void Window_PointerMovedForResize(
        object? sender,
        PointerEventArgs e)
    {
        if (WindowState == WindowState.Maximized || !CanResize)
        {
            Cursor = Cursor.Default;
            return;
        }

        var edge = GetResizeEdge(e.GetPosition(this));
        Cursor = edge switch
        {
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.North => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.West => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            _ => Cursor.Default
        };
    }

    private void Window_PointerPressedForResize(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized
            || !CanResize
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var edge = GetResizeEdge(e.GetPosition(this));
        if (edge is not { } resizeEdge)
            return;

        BeginResizeDrag(resizeEdge, e);
        e.Handled = true;
    }

    private void Window_PointerExitedForResize(
        object? sender,
        PointerEventArgs e)
        => Cursor = Cursor.Default;

    private WindowEdge? GetResizeEdge(Avalonia.Point position)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return null;

        var left = position.X <= ResizeBorderThickness;
        var right = position.X >= width - ResizeBorderThickness;
        var top = position.Y <= ResizeBorderThickness;
        var bottom = position.Y >= height - ResizeBorderThickness;

        if (top && left)
            return WindowEdge.NorthWest;
        if (top && right)
            return WindowEdge.NorthEast;
        if (bottom && left)
            return WindowEdge.SouthWest;
        if (bottom && right)
            return WindowEdge.SouthEast;
        if (top)
            return WindowEdge.North;
        if (bottom)
            return WindowEdge.South;
        if (left)
            return WindowEdge.West;
        if (right)
            return WindowEdge.East;

        return null;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ShowCloseConfirmation();

    private void ShowCloseConfirmation()
    {
        EditUrlOverlay.IsVisible = false;
        CloseConfirmOverlay.IsVisible = true;
    }

    /// <summary>
    /// 供桌面托盘“退出程序”复用与右上角关闭按钮完全相同的确认流程。
    /// </summary>
    public void RequestCloseConfirmation()
        => ShowCloseConfirmation();

    private void CloseCancelButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
        => CloseConfirmOverlay.IsVisible = false;

    private void CloseMinimizeToTrayButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseConfirmOverlay.IsVisible = false;

        if (MinimizeToTrayRequested is { } handler)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        WindowState = WindowState.Minimized;
    }

    private async void CloseConfirmButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
        CloseConfirmOverlay.IsVisible = false;
        try
        {
            if (DataContext is MainWindowViewModel viewModel)
                await viewModel.PrepareForShutdownAsync();

            _allowClose = true;
            Close();
        }
        catch
        {
            CloseConfirmOverlay.IsVisible = true;
        }
        finally
        {
            _shutdownInProgress = false;
        }
    }


    private async void CheckTargetButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TryGetMonitorTarget(sender, out var target)
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.CheckTargetCommand.ExecuteAsync(target);
        }
    }

    private async void RemoveMonitorButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TryGetMonitorTarget(sender, out var target)
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RemoveMonitorCommand.ExecuteAsync(target);
        }
    }

    private async void CopyStreamButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!TryGetMonitorTarget(sender, out var target)
            || string.IsNullOrWhiteSpace(target.StreamUrl))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(target.StreamUrl);
    }

    private void MonitorItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Border { DataContext: LiveMonitorTarget target }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // Double-clicking an action button or the enable switch should perform only
        // that action, not also open the author's folder.
        var current = e.Source as Control;
        while (current is not null && !ReferenceEquals(current, sender))
        {
            if (current is Button or ToggleSwitch)
                return;

            current = current.Parent as Control;
        }

        try
        {
            OpenFolder(viewModel.GetMonitorFolderPath(target));
            e.Handled = true;
        }
        catch
        {
            // Folder-opening failure must not interrupt monitoring.
        }
    }

    private void OpenDownloadRootButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        try
        {
            OpenFolder(viewModel.DownloadRoot);
        }
        catch
        {
            // Folder-opening failure must not interrupt monitoring.
        }
    }

    private static void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            return;
        }

        throw new PlatformNotSupportedException("当前桌面系统不支持打开文件夹。");
    }


    private static bool TryGetMonitorTarget(
        object? sender,
        out LiveMonitorTarget target)
    {
        target = sender switch
        {
            Button { Tag: LiveMonitorTarget item } => item,
            MenuItem { Tag: LiveMonitorTarget item } => item,
            _ => null!
        };

        return target is not null;
    }


    private void EditAuthorInfoButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!TryGetMonitorTarget(sender, out var target))
            return;

        _editingUrlTarget = target;
        EditUrlTargetText.Text = string.IsNullOrWhiteSpace(target.AuthorId)
            ? $"当前：{target.DisplayName}"
            : $"当前：{target.DisplayName} · 作者ID：{target.AuthorId}";
        EditAuthorNameTextBox.Text = target.DisplayName;
        EditAvatarUrlTextBox.Text = target.AvatarUrl ?? string.Empty;
        EditUrlTextBox.Text = target.ProfileUrl;
        EditUrlErrorText.Text = string.Empty;
        EditUrlErrorText.IsVisible = false;
        CloseConfirmOverlay.IsVisible = false;
        EditUrlOverlay.IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            EditAuthorNameTextBox.Focus();
            EditAuthorNameTextBox.SelectAll();
        });
    }

    private void EditUrlCancelButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
        => CloseEditUrlOverlay();

    private async void EditUrlConfirmButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
        => await SaveEditedUrlAsync();

    private async void EditUrlTextBox_KeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseEditUrlOverlay();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveEditedUrlAsync();
        }
    }

    private async Task SaveEditedUrlAsync()
    {
        if (_editingUrlTarget is not { } target
            || DataContext is not MainWindowViewModel viewModel)
        {
            CloseEditUrlOverlay();
            return;
        }

        var error = await viewModel.UpdateMonitorAuthorInfoAsync(
            target,
            EditAuthorNameTextBox.Text ?? string.Empty,
            EditAvatarUrlTextBox.Text ?? string.Empty,
            EditUrlTextBox.Text ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(error))
        {
            EditUrlErrorText.Text = error;
            EditUrlErrorText.IsVisible = true;
            return;
        }

        CloseEditUrlOverlay();
    }

    private void CloseEditUrlOverlay()
    {
        EditUrlOverlay.IsVisible = false;
        EditUrlErrorText.IsVisible = false;
        EditUrlErrorText.Text = string.Empty;
        EditAuthorNameTextBox.Text = string.Empty;
        EditAvatarUrlTextBox.Text = string.Empty;
        EditUrlTextBox.Text = string.Empty;
        _editingUrlTarget = null;
    }


    private async void OpenProfileButton_Click(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!TryGetMonitorTarget(sender, out var target)
            || string.IsNullOrWhiteSpace(target.ProfileUrl)
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        await viewModel.OpenBrowserAsync(target.ProfileUrl);
    }
}
