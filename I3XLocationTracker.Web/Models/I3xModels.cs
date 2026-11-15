using System.Text.Json;
using System.Text.Json.Serialization;

namespace I3XLocationTracker.Models;

public sealed class I3xInfoResponse
{
    [JsonPropertyName("specVersion")] public string? SpecVersion { get; set; }
    [JsonPropertyName("serverVersion")] public string? ServerVersion { get; set; }
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
}

public sealed class ObjectsResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("result")] public List<I3xObjectInfo> Result { get; set; } = new();
}

public sealed class I3xObjectInfo
{
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("typeElementId")] public string? TypeElementId { get; set; }
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }
    [JsonPropertyName("isComposition")] public bool IsComposition { get; set; }
    [JsonPropertyName("isExtended")] public bool IsExtended { get; set; }
}

public sealed class ValueQueryRequest
{
    [JsonPropertyName("elementIds")] public List<string> ElementIds { get; set; } = new();
    [JsonPropertyName("maxDepth")] public int MaxDepth { get; set; } = 1;
}

public sealed class ValueQueryResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("results")] public List<ValueQueryResult> Results { get; set; } = new();
}

public sealed class ValueQueryResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
    [JsonPropertyName("result")] public ValueResultDetail? Result { get; set; }
    [JsonPropertyName("responseDetail")] public JsonElement? ResponseDetail { get; set; }
}

public sealed class ValueResultDetail
{
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
}

// ----- PUT /objects/value (write current value) -----

public sealed class WriteValueRequest
{
    [JsonPropertyName("updates")] public List<WriteValueUpdate> Updates { get; set; } = new();
}

public sealed class WriteValueUpdate
{
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
    [JsonPropertyName("value")] public WriteVqt Value { get; set; } = new();
}

/// <summary>Value / quality / timestamp, as the i3X write API expects.</summary>
public sealed class WriteVqt
{
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
}

public sealed class WriteValueResponse
{
    [JsonPropertyName("success")] public bool? Success { get; set; }
    [JsonPropertyName("results")] public List<WriteValueResult>? Results { get; set; }
    [JsonPropertyName("responseDetail")] public JsonElement? ResponseDetail { get; set; }
}

public sealed class WriteValueResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
    [JsonPropertyName("responseDetail")] public JsonElement? ResponseDetail { get; set; }
}

public sealed class CreateSubscriptionRequest
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class CreateSubscriptionResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("result")] public SubscriptionInfo? Result { get; set; }
}

public sealed class SubscriptionInfo
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("subscriptionId")] public string SubscriptionId { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class RegisterRequest
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("subscriptionId")] public string SubscriptionId { get; set; } = "";
    [JsonPropertyName("elementIds")] public List<string> ElementIds { get; set; } = new();
    [JsonPropertyName("maxDepth")] public int? MaxDepth { get; set; }
}

public sealed class RegisterResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("results")] public List<RegisterResult> Results { get; set; } = new();
}

public sealed class RegisterResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
}

public sealed class StreamRequest
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("subscriptionId")] public string SubscriptionId { get; set; } = "";
}

/// <summary>One live update pushed over the SSE stream for a registered element.</summary>
public sealed class SubscriptionUpdate
{
    [JsonPropertyName("elementId")] public string ElementId { get; set; } = "";
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonPropertyName("quality")] public string? Quality { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
}

public sealed class DeleteSubscriptionsRequest
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("subscriptionIds")] public List<string> SubscriptionIds { get; set; } = new();
}

/// <summary>
/// One reading from a "Locations" typed object's current value, whose fields are all at the top level,
/// e.g. {"Timestamp":..,"X":..,"Y":..,"Z":..,"Battery":..,"IsMoving":..,"Product":"NONE"}
/// </summary>
public sealed class LocationsReading
{
    public DateTime TimestampUtc { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Z { get; set; }
    public double? Battery { get; set; }
    public bool? IsMoving { get; set; }
    public string? Product { get; set; }

    /// <summary>Returns the value's single reading, or an empty list if it has no numeric X and Y.</summary>
    public static List<LocationsReading> ParseFrom(JsonElement value)
    {
        var readings = new List<LocationsReading>();
        if (value.ValueKind != JsonValueKind.Object) return readings;

        var x = GetNumber(value, "X");
        var y = GetNumber(value, "Y");
        if (x == null || y == null) return readings;

        var reading = new LocationsReading
        {
            X = x.Value,
            Y = y.Value,
            Z = GetNumber(value, "Z"),
            Battery = GetNumber(value, "Battery"),
        };

        if (value.TryGetProperty("Timestamp", out var tsEl))
        {
            if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var epochMs))
                reading.TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            else if (tsEl.ValueKind == JsonValueKind.String && DateTime.TryParse(tsEl.GetString(), out var dt))
                reading.TimestampUtc = dt.ToUniversalTime();
        }
        if (reading.TimestampUtc == default) reading.TimestampUtc = DateTime.UtcNow;

        if (value.TryGetProperty("IsMoving", out var movEl) && movEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            reading.IsMoving = movEl.GetBoolean();
        if (value.TryGetProperty("Product", out var prodEl) && prodEl.ValueKind == JsonValueKind.String)
            reading.Product = prodEl.GetString();

        readings.Add(reading);
        return readings;
    }

    private static double? GetNumber(JsonElement value, string name) =>
        value.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d) ? d : null;
}
