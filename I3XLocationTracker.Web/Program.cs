using System.Text.Json;
using I3XLocationTracker.Models;
using I3XLocationTracker.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A service starts in System32, so it must look for wwwroot next to its own exe.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default,
});
builder.Host.UseWindowsService(options => options.ServiceName = "I3XLocationTrackerWeb");

// Test program: open to every computer on the network over plain HTTP, with no login.
// Anyone who can reach this port can change the connection settings and command the robots.
builder.WebHost.UseUrls("http://*:5085");

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var streamRetryDelay = TimeSpan.FromSeconds(3);
var sseJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ----- Settings (shared with the desktop app; the token never leaves the server) -----

app.MapGet("/api/settings", () => ToDto(SettingsService.Load()));

app.MapPut("/api/settings", (SettingsUpdate u) =>
{
    var (_, savedToken) = SettingsService.Load();
    var settings = new AppSettings
    {
        BaseUrl = u.BaseUrl,
        AuthScheme = u.AuthScheme,
        ApiKeyHeader = u.ApiKeyHeader,
        TypeFilter = u.TypeFilter,
        WriteElementId = u.WriteElementId,
        WriteCommand = u.WriteCommand,
        WriteColor = u.WriteColor,
    };
    SettingsService.Save(settings, string.IsNullOrEmpty(u.Token) ? savedToken : u.Token);
    return ToDto(SettingsService.Load());
});

// ----- i3X calls, made server-side with the saved connection settings -----

app.MapPost("/api/connect", (CancellationToken ct) => CallI3x(async client =>
{
    var info = await client.GetInfoAsync(ct);
    return Results.Ok(new { info.ServerName, info.SpecVersion });
}));

app.MapGet("/api/objects", (string? type, CancellationToken ct) => CallI3x(async client =>
    Results.Ok(await client.GetObjectsAsync(type, ct))));

app.MapPost("/api/write", (WriteRequest request, CancellationToken ct) => CallI3x(async client =>
{
    await client.WriteCurrentValueAsync(request.ElementId, request.Value, ct);
    return Results.Ok(new { success = true });
}));

// Bridges one i3X subscription to the browser as Server-Sent Events for as long as the page stays connected.
app.MapGet("/api/stream", async (string[] id, HttpContext context) =>
{
    var ct = context.RequestAborted;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";

    async Task Send(string eventName, object payload)
    {
        await context.Response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, sseJson)}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    using var client = CreateClient();
    var clientId = $"i3x-locations-tracker-web-{Guid.NewGuid():N}";
    string subscriptionId;
    try
    {
        subscriptionId = await client.CreateSubscriptionAsync(clientId, "I3X Locations Tracker (Web)", ct);
        await client.RegisterElementsAsync(clientId, subscriptionId, id, maxDepth: 1, ct);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
        await Send("fatal", $"Failed to start streaming: {ex.Message}");
        return;
    }

    try
    {
        await Send("status", "Streaming live…");
        var reconnecting = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var updates in client.StreamUpdatesAsync(clientId, subscriptionId, ct))
                {
                    if (reconnecting)
                    {
                        reconnecting = false;
                        await Send("status", "Streaming live…");
                    }
                    await Send("update", updates.SelectMany(ToStreamUpdates));
                }
                await Send("status", "Stream closed by server; reconnecting…");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                await Send("status", $"Stream error: {ex.Message} — reconnecting…");
            }
            reconnecting = true;
            await Task.Delay(streamRetryDelay, ct);
        }
    }
    catch (Exception) when (ct.IsCancellationRequested)
    {
        // The browser closed the page or pressed Stop Tracking.
    }
    finally
    {
        await client.DeleteSubscriptionAsync(clientId, subscriptionId, CancellationToken.None);
    }
});

app.Run();

static I3xClient CreateClient()
{
    var (settings, token) = SettingsService.Load();
    var scheme = Enum.TryParse<I3xAuthScheme>(settings.AuthScheme, out var parsed) ? parsed : I3xAuthScheme.None;
    return new I3xClient(settings.BaseUrl, scheme, token, settings.ApiKeyHeader);
}

static async Task<IResult> CallI3x(Func<I3xClient, Task<IResult>> call)
{
    try
    {
        using var client = CreateClient();
        return await call(client);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
}

static IEnumerable<StreamUpdate> ToStreamUpdates(SubscriptionUpdate update)
{
    var readings = LocationsReading.ParseFrom(update.Value);
    if (readings.Count == 0)
        return new[] { new StreamUpdate(update.ElementId, Error: "no X/Y data") };

    return readings.Select(r => new StreamUpdate(
        update.ElementId, r.X, r.Y, r.Battery, r.IsMoving, r.Product,
        new DateTimeOffset(r.TimestampUtc).ToUnixTimeMilliseconds()));
}

static SettingsDto ToDto((AppSettings Settings, string Token) saved) => new(
    saved.Settings.BaseUrl, saved.Settings.AuthScheme, saved.Settings.ApiKeyHeader, saved.Settings.TypeFilter,
    saved.Settings.WriteElementId, saved.Settings.WriteCommand, saved.Settings.WriteColor,
    HasToken: !string.IsNullOrEmpty(saved.Token));

record SettingsDto(string BaseUrl, string AuthScheme, string ApiKeyHeader, string TypeFilter,
    string WriteElementId, string WriteCommand, string WriteColor, bool HasToken);

record SettingsUpdate(string BaseUrl, string AuthScheme, string ApiKeyHeader, string TypeFilter,
    string WriteElementId, string WriteCommand, string WriteColor, string? Token);

record WriteRequest(string ElementId, JsonElement Value);

record StreamUpdate(string ElementId, double? X = null, double? Y = null, double? Battery = null,
    bool? IsMoving = null, string? Product = null, long? Timestamp = null, string? Error = null);
