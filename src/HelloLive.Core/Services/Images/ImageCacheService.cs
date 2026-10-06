using Avalonia.Media.Imaging;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace HelloLive.Core.Services.Images;

public sealed class ImageCacheService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _inflight =
        new(StringComparer.Ordinal);

    public ImageCacheService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "image-cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public Task<Bitmap?> LoadAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        var lazy = _inflight.GetOrAdd(
            url,
            static (key, service) => new Lazy<Task<Bitmap?>>(
                () => service.LoadTrackedAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);

        return lazy.Value.WaitAsync(cancellationToken);
    }

    private async Task<Bitmap?> LoadTrackedAsync(string url)
    {
        try
        {
            return await LoadCoreAsync(url);
        }
        finally
        {
            _inflight.TryRemove(url, out _);
        }
    }

    private async Task<Bitmap?> LoadCoreAsync(string url)
    {
        var filePath = Path.Combine(_cacheDirectory, CreateCacheKey(url) + ".img");
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                var tempPath = filePath + ".tmp";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = ResolveReferrer(url);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(
                                 tempPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output);
                    await output.FlushAsync();
                }

                File.Move(tempPath, filePath, true);
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            return new Bitmap(stream);
        }
        catch
        {
            TryDelete(filePath);
            TryDelete(filePath + ".tmp");
            return null;
        }
    }

    private static Uri ResolveReferrer(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Host.Contains("yximgs", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("gifshow", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("kuaishou", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://www.kuaishou.com/");
        }

        return new Uri("https://www.kuaishou.com/");
    }

    private static string CreateCacheKey(string url)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _inflight.Clear();
        _httpClient.Dispose();
    }
}
