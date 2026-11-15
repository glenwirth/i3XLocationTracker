using System.Text.Json.Serialization;

namespace I3XLocationTracker.Models;

/// <summary>The connection dialog's fields, persisted between app runs.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "http://localhost:8885/i3x/v1";
    [JsonPropertyName("authScheme")] public string AuthScheme { get; set; } = "None";
    [JsonPropertyName("apiKeyHeader")] public string ApiKeyHeader { get; set; } = "X-API-Key";
    [JsonPropertyName("typeFilter")] public string TypeFilter { get; set; } = "type:Locations";

    // Write Command panel
    [JsonPropertyName("writeElementId")] public string WriteElementId { get; set; } = "";
    [JsonPropertyName("writeCommand")] public string WriteCommand { get; set; } = "STOP";
    [JsonPropertyName("writeColor")] public string WriteColor { get; set; } = "NONE";

    /// <summary>DPAPI-protected (current user), Base64-encoded. Empty when no token is set. Never plain text.</summary>
    [JsonPropertyName("protectedToken")] public string ProtectedToken { get; set; } = "";
}
