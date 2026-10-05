using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HelloLive.Core.Remote.Services;
using HelloLive.Core.Remote.ViewModels;
using HelloLive.Core.Remote.Views;

namespace HelloLive.Core.Remote;

public partial class RemoteApp : Application
{
    private RemoteMainViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var viewModel = _viewModel ??= new RemoteMainViewModel(new RemoteLiveClient());

        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new RemoteMainView
            {
                DataContext = viewModel
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new RemoteMainView
            {
                DataContext = viewModel
            };
        }

        if (ApplicationLifetime is IControlledApplicationLifetime controlled)
        {
            controlled.Exit += async (_, _) =>
            {
                if (_viewModel is not null)
                    await _viewModel.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
