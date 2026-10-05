using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelloLive.Core.Contracts;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RemoteHealthDto))]
[JsonSerializable(typeof(RemoteLiveSnapshot))]
[JsonSerializable(typeof(RemoteMonitorDto))]
[JsonSerializable(typeof(RemoteAddMonitorRequest))]
[JsonSerializable(typeof(RemoteMonitorEnabledRequest))]
[JsonSerializable(typeof(RemoteCommandResult))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<RemoteMonitorDto>))]
internal partial class RemoteJsonContext : JsonSerializerContext
{
}
