using HelloLive.Core.Models;

namespace HelloLive.Core.Sites;

public sealed class LivePlatformRegistry
{
    private readonly IReadOnlyDictionary<string, ILivePlatformAdapter> _byId;
    private readonly IReadOnlyList<ILivePlatformAdapter> _adapters;

    public LivePlatformRegistry(IEnumerable<ILivePlatformAdapter> adapters)
    {
        _adapters = adapters.ToArray();
        _byId = _adapters.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PlatformOption> Platforms => _adapters
        .Select(x => new PlatformOption(x.Id, x.DisplayName, x.HomeUrl))
        .OrderBy(x => x.DisplayName)
        .ToArray();

    public ILivePlatformAdapter GetRequired(string id)
        => _byId.TryGetValue(id, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"未注册直播平台适配器：{id}");

    public ILivePlatformAdapter? ResolveByProfileUrl(string url)
        => _adapters.FirstOrDefault(x => x.CanHandleProfileUrl(url));
}
