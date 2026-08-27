using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shared.Common;

/// <summary>
/// The single JSON contract used for every message crossing the broker.
/// </summary>
/// <remarks>
/// Publisher and consumer live in different services and are deployed independently.
/// Sharing one <see cref="JsonSerializerOptions"/> instance is what keeps their wire
/// format from drifting apart.
/// </remarks>
public static class EventSerialization
{
    /// <summary>
    /// Serializer options for broker payloads: camelCase names, no indentation.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
