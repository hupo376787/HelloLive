using Avalonia.Controls;
using Avalonia.Input;
using HelloLive.Core.Models;
using HelloLive.Core.ViewModels;
using System.Diagnostics;

namespace HelloLive.Core.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
        if (sender is Button { Tag: LiveMonitorTarget target }
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.CheckTargetCommand.ExecuteAsync(target);
        }
    }

    private async void RemoveMonitorButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: LiveMonitorTarget target }
            && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.RemoveMonitorCommand.ExecuteAsync(target);
        }
    }

    private async void CopyStreamButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LiveMonitorTarget target }
            || string.IsNullOrWhiteSpace(target.StreamUrl))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(target.StreamUrl);
    }

    private void OpenProfileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LiveMonitorTarget target }
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
