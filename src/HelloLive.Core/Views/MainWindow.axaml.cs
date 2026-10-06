using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using HelloLive.Core.Models;
using HelloLive.Core.ViewModels;
using System.Diagnostics;

namespace HelloLive.Core.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _shutdownInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Closing(
        object? sender,
        WindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
        try
        {
            if (DataContext is MainWindowViewModel viewModel)
                await viewModel.PrepareForShutdownAsync();

            _allowClose = true;
            Close();
        }
        finally
        {
            _shutdownInProgress = false;
        }
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
        => Close();


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


    private void OpenProfileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!TryGetMonitorTarget(sender, out var target)
            || string.IsNullOrWhiteSpace(target.ProfileUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target.ProfileUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // 不让系统浏览器启动失败影响监控主流程。
        }
    }
}
