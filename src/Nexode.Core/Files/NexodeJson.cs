using System.Text.Json;
using System.Text.Json.Serialization;
using Nexode.Core.Model;
using Nexode.Core.Sync;

namespace Nexode.Core.Files;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    Converters = [typeof(HlcJsonConverter)])]
[JsonSerializable(typeof(NexodeFile))]
[JsonSerializable(typeof(ConnectionsPayload))]
[JsonSerializable(typeof(List<Entry>))]
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(Category))]
[JsonSerializable(typeof(List<Category>))]
[JsonSerializable(typeof(EnvironmentDef))]
[JsonSerializable(typeof(ConnectionProfile))]
[JsonSerializable(typeof(EntryTemplate))]
[JsonSerializable(typeof(Dictionary<Guid, HostInfo>))]
[JsonSerializable(typeof(LanSnapshot))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(Nexode.Core.Security.VaultHeader))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class NexodeJson : JsonSerializerContext;

public sealed class HlcJsonConverter : JsonConverter<Hlc>
{
    public override Hlc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Hlc.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, Hlc value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
