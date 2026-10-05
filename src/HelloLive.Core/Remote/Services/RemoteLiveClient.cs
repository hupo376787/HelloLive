using System.Net;
using System.Net.Http.Json;
using HelloLive.Core.Contracts;

namespace HelloLive.Core.Remote.Services;

public sealed class RemoteLiveClient : IDisposable
{
    private const string TokenHeader = "X-HelloLive-Token";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private ConnectionOptions? _options;

    public void Configure(string serverAddress, string token)
    {
        if (!Uri.TryCreate(serverAddress.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("服务器地址必须是 http:// 或 https:// 地址。", nameof(serverAddress));
        }

        if ((OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            && IsLoopbackHost(baseAddress.Host))
        {
            throw new ArgumentException(
                "手机端不能使用 127.0.0.1、::1 或 localhost；请填写桌面端远程控制区域显示的局域网地址。",
                nameof(serverAddress));
        }

        Volatile.Write(ref _options, new ConnectionOptions(baseAddress, token.Trim()));
    }

    public async Task<RemoteHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/health", includeToken: false);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync(
                   RemoteJsonContext.Default.RemoteHealthDto,
                   cancellationToken)
               ?? throw new InvalidOperationException("服务器没有返回健康状态。");
    }

    public async Task<RemoteLiveSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/snapshot");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync(
                   RemoteJsonContext.Default.RemoteLiveSnapshot,
                   cancellationToken)
               ?? throw new InvalidOperationException("服务器没有返回监控状态。");
    }

    public Task<RemoteCommandResult> ExecuteActionAsync(
        string action,
        CancellationToken cancellationToken = default)
        => SendCommandAsync(HttpMethod.Post, $"api/actions/{Uri.EscapeDataString(action)}", null, cancellationToken);

    public Task<RemoteCommandResult> CheckMonitorAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SendCommandAsync(HttpMethod.Post, $"api/monitors/{Uri.EscapeDataString(id)}/check", null, cancellationToken);

    public Task<RemoteCommandResult> RemoveMonitorAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SendCommandAsync(HttpMethod.Delete, $"api/monitors/{Uri.EscapeDataString(id)}", null, cancellationToken);

    public Task<RemoteCommandResult> AddMonitorAsync(
        RemoteAddMonitorRequest requestDto,
        CancellationToken cancellationToken = default)
        => SendCommandAsync(
            HttpMethod.Post,
            "api/monitors",
            JsonContent.Create(requestDto, RemoteJsonContext.Default.RemoteAddMonitorRequest),
            cancellationToken);

    public Task<RemoteCommandResult> SetMonitorEnabledAsync(
        string id,
        bool isEnabled,
        CancellationToken cancellationToken = default)
        => SendCommandAsync(
            HttpMethod.Put,
            $"api/monitors/{Uri.EscapeDataString(id)}/enabled",
            JsonContent.Create(
                new RemoteMonitorEnabledRequest { IsEnabled = isEnabled },
                RemoteJsonContext.Default.RemoteMonitorEnabledRequest),
            cancellationToken);

    private async Task<RemoteCommandResult> SendCommandAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Content = content;
        using var response = await SendAsync(request, cancellationToken);

        RemoteCommandResult? result = null;
        try
        {
            result = await response.Content.ReadFromJsonAsync(
                RemoteJsonContext.Default.RemoteCommandResult,
                cancellationToken);
        }
        catch
        {
        }

        result ??= RemoteCommandResult.Fail(
            $"桌面主机返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。");

        if (!response.IsSuccessStatusCode)
            result.Success = false;

        return result;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        bool includeToken = true)
    {
        var options = Volatile.Read(ref _options)
                      ?? throw new InvalidOperationException("请先填写主机地址并点击连接。");

        var request = new HttpRequestMessage(method, new Uri(options.BaseAddress, relativePath));
        if (includeToken && !string.IsNullOrWhiteSpace(options.Token))
            request.Headers.TryAddWithoutValidation(TokenHeader, options.Token);

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接桌面主机超时，请确认远程服务器已开启且地址、端口正确。");
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(BuildNetworkErrorMessage(request.RequestUri, ex), ex);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("远程访问令牌不正确。");

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(text)
                ? $"桌面主机返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。"
                : text,
            null,
            response.StatusCode);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address)
               && IPAddress.IsLoopback(address);
    }

    private static string BuildNetworkErrorMessage(Uri? uri, HttpRequestException exception)
    {
        var target = uri is null ? "桌面主机" : $"桌面主机 {uri.GetLeftPart(UriPartial.Authority)}";
        var original = exception.Message;

        if (original.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase)
            || original.Contains("TypeError", StringComparison.OrdinalIgnoreCase)
            || original.Contains("net_http", StringComparison.OrdinalIgnoreCase))
        {
            return $"无法访问{target}。请确认桌面端已开启远程控制服务器；手机或其他电脑必须填写桌面端显示的局域网 IP。";
        }

        return $"无法访问{target}：{original}";
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record ConnectionOptions(Uri BaseAddress, string Token);
}
