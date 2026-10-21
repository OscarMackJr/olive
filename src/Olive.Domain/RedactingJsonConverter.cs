using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olive.Domain;

public sealed class RedactingJsonConverter<T> : JsonConverter<T> where T : class
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Secrets are never deserialized.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue("[REDACTED]");
}
