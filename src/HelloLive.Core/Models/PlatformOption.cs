namespace HelloLive.Core.Models;

public sealed record PlatformOption(string Id, string DisplayName, string HomeUrl)
{
    public override string ToString() => DisplayName;
}
