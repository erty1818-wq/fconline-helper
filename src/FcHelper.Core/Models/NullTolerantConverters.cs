using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FcHelper.Core.Models;

// Real match-detail responses send null for every stat of a side that quit before the match produced any
// (a forfeit loss): numbers, booleans and the controller string alike. The model keeps plain value types,
// so these converters read null as the default value instead of failing the whole match.
// Registered on FcJsonContext; numbers sent as strings are accepted too, as AllowReadingFromString did.

public sealed class NullAsZeroInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => 0,
        JsonTokenType.String => int.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
        _ => reader.TryGetInt32(out var v) ? v : (int)reader.GetDouble(),
    };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public sealed class NullAsZeroInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => 0,
        JsonTokenType.String => long.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
        _ => reader.TryGetInt64(out var v) ? v : (long)reader.GetDouble(),
    };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public sealed class NullAsZeroDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => 0,
        JsonTokenType.String => double.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
        _ => reader.GetDouble(),
    };

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

public sealed class NullAsFalseBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType != JsonTokenType.Null && reader.GetBoolean();

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

public sealed class NullAsEmptyStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? "" : reader.GetString() ?? "";

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
