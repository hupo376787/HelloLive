using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using HelloLive.Core.Contracts;
using HelloLive.Core.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HelloLive.Desktop.Remote;

public sealed class RemoteApiHostService : IAsyncDisposable
{
    private const string TokenHeader = "X-HelloLive-Token";

    private readonly MainWindowViewModel _viewModel;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;

    public RemoteApiHostService(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
                await StartCoreAsync(cancellationToken);
            else
                await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            if (_viewModel.RemoteApiEnabled)
                await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_application is not null)
            return;

        WebApplication? app = null;
        try
        {
            _viewModel.SetRemoteApiStatusText("正在启动远程控制服务器…");

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://0.0.0.0:{_viewModel.RemoteApiPort}");
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader());
            });

            app = builder.Build();
            app.UseCors();

            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsOptions(context.Request.Method)
                    || string.Equals(
                        context.Request.Path.Value,
                        "/api/health",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                var token = context.Request.Headers[TokenHeader].FirstOrDefault();
                if (!CryptographicEquals(token, _viewModel.RemoteApiToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(
                        RemoteCommandResult.Fail("远程访问令牌不正确。"));
                    return;
                }

                await next();
            });

            app.MapGet("/api/health", () => new RemoteHealthDto
            {
                Service = "HelloLive",
                Version = "1.0",
                ServerTime = DateTimeOffset.Now
            });

            app.MapGet("/api/snapshot", async () =>
                await InvokeOnUi(_viewModel.CreateRemoteSnapshot));

            app.MapPost("/api/actions/{action}", async (string action) =>
            {
                try
                {
                    switch (action.Trim().ToLowerInvariant())
                    {
                        case "start":
                            await InvokeOnUiAsync(_viewModel.StartRemoteMonitoringAsync);
                            return RemoteCommandResult.Ok("已发送开始监控命令。");

                        case "stop":
                            await InvokeOnUiAsync(_viewModel.StopRemoteMonitoringAsync);
                            return RemoteCommandResult.Ok("已停止监控。");

                        case "check-all":
                            await InvokeOnUiAsync(_viewModel.CheckAllRemoteAsync);
                            return RemoteCommandResult.Ok("已完成一次全部检查。");

                        default:
                            return RemoteCommandResult.Fail($"未知远程命令：{action}");
                    }
                }
                catch (Exception ex)
                {
                    return RemoteCommandResult.Fail(ex.Message);
                }
            });

            app.MapPost("/api/monitors", async (RemoteAddMonitorRequest request) =>
            {
                try
                {
                    var message = await InvokeOnUiAsync(
                        () => _viewModel.AddRemoteMonitorAsync(request.Url, request.Name));
                    return RemoteCommandResult.Ok(message);
                }
                catch (Exception ex)
                {
                    return RemoteCommandResult.Fail(ex.Message);
                }
            });

            app.MapPut("/api/monitors/{id}/enabled", async (
                string id,
                RemoteMonitorEnabledRequest request) =>
            {
                try
                {
                    var message = await InvokeOnUiAsync(
                        () => _viewModel.SetRemoteMonitorEnabledAsync(id, request.IsEnabled));
                    return RemoteCommandResult.Ok(message);
                }
                catch (Exception ex)
                {
                    return RemoteCommandResult.Fail(ex.Message);
                }
            });

            app.MapPost("/api/monitors/{id}/check", async (string id) =>
            {
                try
                {
                    var message = await InvokeOnUiAsync(
                        () => _viewModel.CheckRemoteMonitorAsync(id));
                    return RemoteCommandResult.Ok(message);
                }
                catch (Exception ex)
                {
                    return RemoteCommandResult.Fail(ex.Message);
                }
            });

            app.MapDelete("/api/monitors/{id}", async (string id) =>
            {
                try
                {
                    var message = await InvokeOnUiAsync(
                        () => _viewModel.RemoveRemoteMonitorAsync(id));
                    return RemoteCommandResult.Ok(message);
                }
                catch (Exception ex)
                {
                    return RemoteCommandResult.Fail(ex.Message);
                }
            });

            await app.StartAsync(cancellationToken);
            _application = app;

            var addresses = GetLanAddresses()
                .Select(ip => $"http://{ip}:{_viewModel.RemoteApiPort}")
                .ToArray();
            var addressText = addresses.Length == 0
                ? $"http://127.0.0.1:{_viewModel.RemoteApiPort}"
                : string.Join("、", addresses);

            _viewModel.SetRemoteApiStatusText($"运行中：{addressText}");
            _viewModel.AddRemoteLog($"远程控制服务器已启动：{addressText}");
        }
        catch (Exception ex)
        {
            if (app is not null)
                await app.DisposeAsync();

            _viewModel.SetRemoteApiStatusText($"启动失败：{ex.Message}");
            _viewModel.AddRemoteLog($"远程控制服务器启动失败：{ex.Message}");
        }
    }

    private async Task StopCoreAsync()
    {
        var app = Interlocked.Exchange(ref _application, null);
        if (app is null)
        {
            _viewModel.SetRemoteApiStatusText("远程控制服务器未启动");
            return;
        }

        _viewModel.SetRemoteApiStatusText("正在停止远程控制服务器…");
        try
        {
            await app.StopAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await app.DisposeAsync();
        }

        _viewModel.SetRemoteApiStatusText("远程控制服务器未启动");
        _viewModel.AddRemoteLog("远程控制服务器已停止。");
    }

    private static bool CryptographicEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length
               && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static IEnumerable<string> GetLanAddresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address.Address))
                {
                    yield return address.Address.ToString();
                }
            }
        }
    }

    private static Task<T> InvokeOnUi<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return Task.FromResult(action());

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private static Task InvokeOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
