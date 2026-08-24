using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using I3XLocationTracker.Models;

namespace I3XLocationTracker.Services;

public enum I3xAuthScheme
{
    None,
    Bearer,
    ApiKey
}

/// <summary>
/// Minimal client for the i3X REST API (https://github.com/cesmii/i3X spec).
/// Talks directly to the i3X server's HTTP endpoints (not through MCP).
/// </summary>
public sealed class I3xClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    // Separate client for the long-lived SSE stream request: it must not time out
    // the way a normal short-lived request would.
    private readonly HttpClient _streamHttp;

    public string BaseUrl { get; }

    public I3xClient(string baseUrl, I3xAuthScheme authScheme, string? token, string apiKeyHeader)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _streamHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        foreach (var client in new[] { _http, _streamHttp })
        {
            switch (authScheme)
            {
                case I3xAuthScheme.Bearer when !string.IsNullOrWhiteSpace(token):
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    break;
                case I3xAuthScheme.ApiKey when !string.IsNullOrWhiteSpace(token):
                    client.DefaultRequestHeaders.Add(string.IsNullOrWhiteSpace(apiKeyHeader) ? "X-API-Key" : apiKeyHeader, token);
                    break;
            }
        }
    }

    public async Task<I3xInfoResponse> GetInfoAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/info", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var info = await resp.Content.ReadFromJsonAsync<I3xInfoResponse>(JsonOptions, ct).ConfigureAwait(false);
        return info ?? new I3xInfoResponse();
    }

    /// <summary>
    /// Lists objects, optionally filtered by exact typeElementId. Falls back to an
    /// unfiltered call + client-side filtering if the server ignores/rejects the query param.
    /// </summary>
    public async Task<List<I3xObjectInfo>> GetObjectsAsync(string? typeElementId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/objects";
        if (!string.IsNullOrWhiteSpace(typeElementId))
            url += $"?typeElementId={Uri.EscapeDataString(typeElementId)}";

        var result = await GetObjectsRawAsync(url, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(typeElementId) && result.Count == 0)
        {
            // Fallback: some servers don't support the filter param — filter client-side instead.
            var all = await GetObjectsRawAsync($"{BaseUrl}/objects", ct).ConfigureAwait(false);
            result = all.Where(o =>
                    string.Equals(o.TypeElementId, typeElementId, StringComparison.OrdinalIgnoreCase) ||
                    (o.TypeElementId?.EndsWith(typeElementId, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        return result;
    }

    private async Task<List<I3xObjectInfo>> GetObjectsRawAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var parsed = await resp.Content.ReadFromJsonAsync<ObjectsResponse>(JsonOptions, ct).ConfigureAwait(false);
        return parsed?.Result ?? new List<I3xObjectInfo>();
    }

    // ----- Subscriptions (live push via SSE — no polling) -----

    public async Task<string> CreateSubscriptionAsync(string clientId, string? displayName, CancellationToken ct = default)
    {
        var request = new CreateSubscriptionRequest { ClientId = clientId, DisplayName = displayName };
        using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/subscriptions", request, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var parsed = await resp.Content.ReadFromJsonAsync<CreateSubscriptionResponse>(JsonOptions, ct).ConfigureAwait(false);
        var subscriptionId = parsed?.Result?.SubscriptionId;
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new InvalidOperationException("i3X server did not return a subscriptionId.");
        return subscriptionId;
    }

    public async Task RegisterElementsAsync(string clientId, string subscriptionId, IReadOnlyCollection<string> elementIds, int maxDepth = 1, CancellationToken ct = default)
    {
        if (elementIds.Count == 0) return;

        var request = new RegisterRequest
        {
            ClientId = clientId,
            SubscriptionId = subscriptionId,
            ElementIds = elementIds.ToList(),
            MaxDepth = maxDepth,
        };
        using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/subscriptions/register", request, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var parsed = await resp.Content.ReadFromJsonAsync<RegisterResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (parsed is { Success: false })
            throw new InvalidOperationException("i3X server rejected the subscription registration.");
    }

    /// <summary>
    /// Opens the i3X SSE event stream for a subscription and yields each batch of updates as it
    /// arrives ("data: [...]" frames). Runs until the server closes the connection or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<List<SubscriptionUpdate>> StreamUpdatesAsync(
        string clientId, string subscriptionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/subscriptions/stream")
        {
            Content = JsonContent.Create(new StreamRequest { ClientId = clientId, SubscriptionId = subscriptionId }, options: JsonOptions),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        using (var reader = new StreamReader(stream))
        {
            var dataBuffer = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) yield break; // server closed the connection

                if (line.Length == 0)
                {
                    // Blank line = dispatch the event we've been accumulating.
                    if (dataBuffer.Length > 0)
                    {
                        var json = dataBuffer.ToString();
                        dataBuffer.Clear();

                        List<SubscriptionUpdate>? updates = null;
                        try { updates = JsonSerializer.Deserialize<List<SubscriptionUpdate>>(json, JsonOptions); }
                        catch (JsonException) { /* skip malformed frame */ }

                        if (updates is { Count: > 0 })
                            yield return updates;
                    }
                    continue;
                }

                if (line[0] == ':') continue; // SSE comment / heartbeat — ignore

                if (line.StartsWith("data:", StringComparison.Ordinal))
                    dataBuffer.Append(line.AsSpan(5).TrimStart());
                // other SSE fields (event:, id:, retry:) aren't used by this API — ignore
            }
        }
    }

    public async Task DeleteSubscriptionAsync(string clientId, string subscriptionId, CancellationToken ct = default)
    {
        try
        {
            var request = new DeleteSubscriptionsRequest { ClientId = clientId, SubscriptionIds = new List<string> { subscriptionId } };
            using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/subscriptions/delete", request, JsonOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup (e.g. app closing, server already gone) — never let this fault the caller.
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _streamHttp.Dispose();
    }
}
