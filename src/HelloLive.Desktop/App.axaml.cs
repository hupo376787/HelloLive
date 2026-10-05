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

namespace HelloLive.Desktop;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private PlaywrightLiveBrowserService? _browser;

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
            var coordinator = new LiveMonitorCoordinator(_browser, platforms);
            _viewModel = new MainWindowViewModel(
                _browser,
                platforms,
                coordinator,
                settingsService,
                monitorStore);

            var mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
            desktop.MainWindow = mainWindow;
            desktop.Exit += Desktop_Exit;
            mainWindow.Opened += async (_, _) => await _viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void Desktop_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
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
