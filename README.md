# i3X Locations Tracker

A Windows desktop app (WPF, .NET 8) that connects to an [i3X](https://github.com/cesmii/i3X) server, discovers every object whose type is `Locations`, and plots their live X/Y trajectory in real time — no polling, driven entirely by the i3X push-subscription (SSE) API.

Built for tracking things like AMRs (autonomous mobile robots) that report `{ Timestamp, X, Y, Z, SectorId, Battery, IsMoving }` readings under a `Locations` array.

See [Chat-Transcript.pdf](Chat-Transcript.pdf) for the full conversation this app was designed and built in.

---

## Features

### Connection
- Connects to any i3X server over its versioned REST API (default `http://localhost:8885/i3x/v1`).
- Auth schemes: **None**, **Bearer** token, or **API key** (with a configurable header name, default `X-API-Key`).
- `Connect` validates the server via `GET /info` and reports its name and spec version.

### Discovery
- `Discover` calls `GET /objects` filtered by a configurable **type filter** (default `type:Locations`) to find every matching object.
- Falls back to an unfiltered `GET /objects` + client-side filtering if the server doesn't support the query-param filter.
- Each discovered object gets a distinct color, used consistently across the data grid, trajectory line, and legend.

### Live tracking — SSE streaming, not polling
`Start Tracking` opens a real i3X **subscription** and streams updates as they happen:
1. `POST /subscriptions` — opens a subscription under a generated client ID.
2. `POST /subscriptions/register` — registers every discovered element against it.
3. `POST /subscriptions/stream` — opens the Server-Sent-Events stream and reads it continuously on a background task, parsing `data: [...]` frames as they arrive (SSE comment/heartbeat lines are ignored). Nothing re-fetches values on a timer.
4. If the stream drops or the server closes it, the app automatically reconnects after a 3-second delay.
5. `Stop Tracking` cancels the read loop and best-effort calls `POST /subscriptions/delete` to clean up server-side.

### Trajectory chart
- A single X/Y plot (OxyPlot) — **X on the horizontal axis, Y on the vertical axis** — showing each tracked object's path as a line, with small circle markers along the trail (10-minute rolling window; older points are trimmed automatically).
- A **star marker** highlights each object's current position, distinct from its trail.
- Per-object checkboxes in the data grid ("Track" column) toggle a line's visibility on the chart without dropping its subscription.
- Axes autoscale to the live data range.

### Live data grid
Shows, per discovered object: display name, element ID, latest X/Y, sector, battery, moving flag, last-update timestamp, and a status column (`live`, `no response`, `no Locations data`, etc.) for at-a-glance health.

### Settings persistence
Every connection-dialog field — Base URL, auth scheme, API key header, type filter, and the token/key itself — is saved automatically to:
```
%AppData%\I3XLocationTracker\settings.json
```
- Saves are **debounced** (400ms) so a burst of keystrokes becomes one disk write, not one per character, and are written atomically (write-to-temp then replace) so a crash mid-save can't corrupt the file.
- **The token/key is never written in plain text.** It's encrypted at rest with Windows DPAPI (current-user scope) before it touches disk — readable only by the same Windows user account on the same machine.
- On launch, the dialog is pre-filled from the saved file automatically.

### Dark UI
The whole window — connection panel, buttons, combo box (including its dropdown), checkboxes, data grid, and the trajectory chart itself — uses a consistent dark theme. Opens centered on the screen.

---

## Requirements

- Windows with .NET 8 SDK (or the .NET 8 Desktop Runtime to just run a published build).
- An i3X server reachable over HTTP(S) (tested against a local HighByte Intelligence Hub instance at `http://localhost:8885/i3x/v1`).

## Demo i3X server config

[`intelligencehub-configuration_AMRsAndProductionLine.json`](intelligencehub-configuration_AMRsAndProductionLine.json) is an exported **HighByte Intelligence Hub 4.5** project configuration. Importing it into a HighByte Intelligence Hub instance stands up an i3X server that simulates live AMR `Locations` data (plus a production-line demo) — a quick way to have something for this app to connect to and track without a real AMR fleet.

> **Note:** this file contains a plaintext database credential and an internal server IP for the demo environment it was exported from. Treat it as sensitive if you didn't intend for it to be public.

## Build & run

```bash
cd I3XLocationTracker
dotnet build
dotnet run
```

## Using the app

1. **Connection panel** — enter the i3X base URL, pick an auth scheme, and (if needed) the token/key. Click **Connect**.
2. **Locations Objects panel** — adjust the type filter if needed (default `type:Locations`), click **Discover**.
3. Click **Start Tracking** to open the subscription and begin streaming. The data grid and trajectory chart update live.
4. Uncheck an object's **Track** box to hide its line on the chart without stopping its data.
5. Click **Stop Tracking** to end the subscription cleanly.

Settings are remembered automatically — there's nothing to save manually.

---

## Project structure

```
I3XLocationTracker/
├── MainWindow.xaml / .cs        UI: connection panel, data grid, trajectory chart; dark theme styles/templates
├── ViewModels/
│   ├── MainViewModel.cs         App state, commands, the SSE read loop, settings load/save wiring
│   ├── TrackedObject.cs         One discovered object: its trajectory series, current-position marker, latest reading
│   └── RelayCommand.cs          Minimal ICommand (sync + async) for MVVM bindings
├── Services/
│   ├── I3xClient.cs             HTTP client for the i3X REST API (info, objects, subscriptions, SSE stream)
│   └── SettingsService.cs       Loads/saves settings.json; DPAPI-encrypts the token
├── Models/
│   ├── I3xModels.cs             DTOs for i3X responses + LocationsReading parsing
│   └── AppSettings.cs           Persisted settings shape
└── App.xaml / .cs               Standard WPF entry point
```

### i3X REST endpoints used

| Endpoint | Purpose |
|---|---|
| `GET /info` | Validate the connection; server name/version/capabilities |
| `GET /objects?typeElementId=...` | Discover objects by type |
| `POST /subscriptions` | Open a subscription |
| `POST /subscriptions/register` | Register elements to watch |
| `POST /subscriptions/stream` | Open the SSE push stream |
| `POST /subscriptions/delete` | Tear down a subscription |

(`POST /objects/value` — a one-shot current-value read — also exists in `I3xClient` but isn't used by the tracking flow, which relies entirely on the stream.)

### Expected object shape

Objects of type `Locations` are expected to expose a current value shaped like:
```json
{
  "Locations": [
    { "Timestamp": 1787528602287, "SectorId": 1, "X": 205570, "Y": 189887, "Z": 0, "Battery": 87, "IsMoving": true }
  ]
}
```
`Timestamp` may be epoch milliseconds or an ISO-8601 string; both are handled.

---

## Notes & limitations

- The DPAPI-encrypted token is tied to the current Windows user account and machine — copying `settings.json` elsewhere won't restore a usable token there.
- The 10-minute rolling trajectory window and the color palette (8 colors, cycling) are hardcoded in `TrackedObject.cs` / `MainViewModel.cs` if you want to tune them.
- If the i3X server enforces auth on some endpoints but not others (as some demo servers do), `Connect`/`Discover` may succeed while `Start Tracking` fails with 401 — check the auth scheme/token if that happens.
