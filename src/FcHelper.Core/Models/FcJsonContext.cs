using System.Text.Json.Serialization;

namespace FcHelper.Core.Models;

/// <summary>Source-generated serializers: avoids reflection at startup and keeps memory low.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    Converters = [
        typeof(NullAsZeroInt32Converter), typeof(NullAsZeroInt64Converter), typeof(NullAsZeroDoubleConverter),
        typeof(NullAsFalseBooleanConverter), typeof(NullAsEmptyStringConverter),
    ])]
[JsonSerializable(typeof(MatchDetail))]
[JsonSerializable(typeof(OuidResponse))]
[JsonSerializable(typeof(UserBasic))]
[JsonSerializable(typeof(List<MaxDivision>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<MatchTypeMeta>))]
[JsonSerializable(typeof(List<SpIdMeta>))]
[JsonSerializable(typeof(List<DivisionMeta>))]
[JsonSerializable(typeof(List<SpPositionMeta>))]
[JsonSerializable(typeof(List<SeasonIdMeta>))]
[JsonSerializable(typeof(NexonErrorResponse))]
public sealed partial class FcJsonContext : JsonSerializerContext;

public sealed record NexonErrorResponse
{
    [JsonPropertyName("error")] public NexonError? Error { get; init; }
}

public sealed record NexonError
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
