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
/// One reading extracted from a "Locations" typed object's current value,
/// e.g. {"Locations":[{"Timestamp":..,"SectorId":..,"X":..,"Y":..,"Z":..,"Battery":..,"IsMoving":..}]}
/// </summary>
public sealed class LocationsReading
{
    public DateTime TimestampUtc { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double? Z { get; set; }
    public int? SectorId { get; set; }
    public double? Battery { get; set; }
    public bool? IsMoving { get; set; }

    public static List<LocationsReading> ParseFrom(JsonElement value)
    {
        var readings = new List<LocationsReading>();
        if (value.ValueKind != JsonValueKind.Object) return readings;
        if (!value.TryGetProperty("Locations", out var locationsArray)) return readings;
        if (locationsArray.ValueKind != JsonValueKind.Array) return readings;

        foreach (var item in locationsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("X", out var xEl) || !item.TryGetProperty("Y", out var yEl)) continue;
            if (!xEl.TryGetDouble(out var x) || !yEl.TryGetDouble(out var y)) continue;

            var reading = new LocationsReading { X = x, Y = y };

            if (item.TryGetProperty("Timestamp", out var tsEl))
            {
                if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var epochMs))
                    reading.TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
                else if (tsEl.ValueKind == JsonValueKind.String && DateTime.TryParse(tsEl.GetString(), out var dt))
                    reading.TimestampUtc = dt.ToUniversalTime();
            }
            if (reading.TimestampUtc == default) reading.TimestampUtc = DateTime.UtcNow;

            if (item.TryGetProperty("Z", out var zEl) && zEl.TryGetDouble(out var z)) reading.Z = z;
            if (item.TryGetProperty("SectorId", out var secEl) && secEl.TryGetInt32(out var sec)) reading.SectorId = sec;
            if (item.TryGetProperty("Battery", out var batEl) && batEl.TryGetDouble(out var bat)) reading.Battery = bat;
            if (item.TryGetProperty("IsMoving", out var movEl) && movEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                reading.IsMoving = movEl.GetBoolean();

            readings.Add(reading);
        }

        return readings;
    }
}
