using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HelloLive.Core.Services.Monitoring;
using HelloLive.Core.Services.Settings;
using HelloLive.Core.Sites;
using HelloLive.Core.Sites.Kuaishou;
using HelloLive.Core.ViewModels;
using HelloLive.Core.Views;
using HelloLive.Desktop.Chromium;
using HelloLive.Desktop.Playwright;
using HelloLive.Desktop.Remote;
using HelloLive.Desktop.Recording;

namespace HelloLive.Desktop;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private PlaywrightLiveBrowserService? _browser;
    private RemoteApiHostService? _remoteApiHost;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new SettingsService();
            var monitorStore = new MonitorStore(settingsService.SettingsPath);
            var platforms = new LivePlatformRegistry(new ILivePlatformAdapter[]
            {
                new KuaishouLiveAdapter()
            });

            var installer = new PlaywrightChromiumInstaller();
            _browser = new PlaywrightLiveBrowserService(installer);
            var recorder = new LiveStreamRecorder();
            var coordinator = new LiveMonitorCoordinator(_browser, platforms, recorder);
            _viewModel = new MainWindowViewModel(
                _browser,
                platforms,
                coordinator,
                settingsService,
                monitorStore);
            _remoteApiHost = new RemoteApiHostService(_viewModel);
            _viewModel.RemoteApiEnabledChanged += ViewModel_RemoteApiEnabledChanged;
            _viewModel.RemoteApiPortChanged += ViewModel_RemoteApiPortChanged;

            var mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
            desktop.MainWindow = mainWindow;
            desktop.Exit += Desktop_Exit;
            mainWindow.Opened += async (_, _) =>
            {
                await _viewModel.InitializeAsync();
                if (_remoteApiHost is not null)
                    await _remoteApiHost.SetEnabledAsync(_viewModel.RemoteApiEnabled);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void ViewModel_RemoteApiEnabledChanged(object? sender, bool enabled)
    {
        if (_remoteApiHost is not null)
            await _remoteApiHost.SetEnabledAsync(enabled);
    }

    private async void ViewModel_RemoteApiPortChanged(object? sender, EventArgs e)
    {
        if (_remoteApiHost is not null && _viewModel?.RemoteApiEnabled == true)
            await _remoteApiHost.RestartAsync();
    }

    private async void Desktop_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.RemoteApiEnabledChanged -= ViewModel_RemoteApiEnabledChanged;
            _viewModel.RemoteApiPortChanged -= ViewModel_RemoteApiPortChanged;
        }

        if (_remoteApiHost is not null)
        {
            await _remoteApiHost.DisposeAsync();
            _remoteApiHost = null;
        }

        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
            _viewModel = null;
        }

        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }
    }
}
