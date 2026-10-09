using Avalonia.Controls;
using HelloLive.Core.Contracts;
using HelloLive.Core.Remote.ViewModels;

namespace HelloLive.Core.Remote.Views;

public partial class RemoteMainView : UserControl
{
    public RemoteMainView()
    {
        InitializeComponent();
    }

    private void RemoteMainView_SizeChanged(
        object? sender,
        SizeChangedEventArgs e)
    {
        if (DataContext is RemoteMainViewModel viewModel)
            viewModel.UpdateViewportWidth(e.NewSize.Width);
    }

    private async void CheckMonitor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: RemoteMonitorDto item }
            && DataContext is RemoteMainViewModel viewModel)
        {
            await viewModel.CheckMonitorAsync(item);
        }
    }

    private async void ToggleMonitor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: RemoteMonitorDto item }
            && DataContext is RemoteMainViewModel viewModel)
        {
            await viewModel.ToggleMonitorEnabledAsync(item);
        }
    }

    private async void RemoveMonitor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: RemoteMonitorDto item }
            && DataContext is RemoteMainViewModel viewModel)
        {
            await viewModel.RemoveMonitorAsync(item);
        }
    }
}
