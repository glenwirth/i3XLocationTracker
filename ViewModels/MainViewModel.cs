using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using I3XLocationTracker.Models;
using I3XLocationTracker.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;

namespace I3XLocationTracker.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x33, 0x7a, 0xb7), OxyColor.FromRgb(0xe0, 0x7b, 0x39),
        OxyColor.FromRgb(0x3f, 0xa7, 0x66), OxyColor.FromRgb(0xc0, 0x39, 0x2b),
        OxyColor.FromRgb(0x8e, 0x5a, 0xc4), OxyColor.FromRgb(0xb5, 0x8a, 0x2b),
        OxyColor.FromRgb(0x2b, 0xa6, 0xa6), OxyColor.FromRgb(0xd1, 0x4f, 0x8f),
    };

    private static readonly TimeSpan StreamRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _saveSettingsTimer;

    private I3xClient? _client;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private string? _clientId;
    private string? _subscriptionId;
    private Dictionary<string, TrackedObject> _objectsById = new();

    public ObservableCollection<TrackedObject> Objects { get; } = new();

    /// <summary>Spatial X-Y trajectory plot: one line per tracked object, X on the X axis, Y on the Y axis.</summary>
    public PlotModel TrajectoryPlotModel { get; }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DiscoverCommand { get; }
    public RelayCommand StartTrackingCommand { get; }
    public RelayCommand StopTrackingCommand { get; }
    public RelayCommand SendCommandCommand { get; }
    public RelayCommand RefreshWriteTargetsCommand { get; }

    // ----- Write Command panel -----

    /// <summary>Every object on the server (any type), offered as write targets.</summary>
    public ObservableCollection<WriteTarget> WriteTargets { get; } = new();

    public string[] CommandOptions { get; } = { "GO", "STOP" };
    public string[] ColorOptions { get; } = { "NONE", "BLUE", "RED" };

    private WriteTarget? _selectedWriteTarget;
    public WriteTarget? SelectedWriteTarget
    {
        get => _selectedWriteTarget;
        set
        {
            _selectedWriteTarget = value;
            OnPropertyChanged();
            if (value != null) WriteElementId = value.ElementId;
        }
    }

    private TrackedObject? _selectedObject;
    /// <summary>Row selected in the data grid; selecting one makes it the write target.</summary>
    public TrackedObject? SelectedObject
    {
        get => _selectedObject;
        set
        {
            _selectedObject = value;
            OnPropertyChanged();
            if (value != null) WriteElementId = value.ElementId;
        }
    }

    private string _writeElementId = "";
    public string WriteElementId
    {
        get => _writeElementId;
        set
        {
            _writeElementId = value;
            OnPropertyChanged();
            // Keep the picker in step with a typed / grid-selected id (null when it isn't in the list).
            var match = WriteTargets.FirstOrDefault(t => t.ElementId == value);
            if (!ReferenceEquals(match, _selectedWriteTarget)) { _selectedWriteTarget = match; OnPropertyChanged(nameof(SelectedWriteTarget)); }
            SendCommandCommand?.RaiseCanExecuteChanged();
            ScheduleSave();
        }
    }

    private string _writeCommand = "STOP";
    public string WriteCommand { get => _writeCommand; set { _writeCommand = value; OnPropertyChanged(); RebuildPayload(); ScheduleSave(); } }

    private string _writeColor = "NONE";
    public string WriteColor { get => _writeColor; set { _writeColor = value; OnPropertyChanged(); RebuildPayload(); ScheduleSave(); } }

    private string _writePayload = "";
    /// <summary>The JSON that will be written. Generated from Command/Color, but freely editable before sending.</summary>
    public string WritePayload { get => _writePayload; set { _writePayload = value; OnPropertyChanged(); SendCommandCommand?.RaiseCanExecuteChanged(); } }

    private string _writeStatus = "";
    public string WriteStatus { get => _writeStatus; set { _writeStatus = value; OnPropertyChanged(); } }

    private string _baseUrl = "http://localhost:8885/i3x/v1";
    public string BaseUrl { get => _baseUrl; set { _baseUrl = value; OnPropertyChanged(); ScheduleSave(); } }

    public I3xAuthScheme[] AuthSchemes { get; } = (I3xAuthScheme[])Enum.GetValues(typeof(I3xAuthScheme));

    private I3xAuthScheme _authScheme = I3xAuthScheme.None;
    public I3xAuthScheme AuthScheme { get => _authScheme; set { _authScheme = value; OnPropertyChanged(); ScheduleSave(); } }

    private string _token = "";
    public string Token { get => _token; set { _token = value; OnPropertyChanged(); ScheduleSave(); } }

    private string _apiKeyHeader = "X-API-Key";
    public string ApiKeyHeader { get => _apiKeyHeader; set { _apiKeyHeader = value; OnPropertyChanged(); ScheduleSave(); } }

    private string _typeFilter = "type:Locations";
    public string TypeFilter { get => _typeFilter; set { _typeFilter = value; OnPropertyChanged(); ScheduleSave(); } }

    private string _connectionStatus = "Not connected.";
    public string ConnectionStatus { get => _connectionStatus; set { _connectionStatus = value; OnPropertyChanged(); } }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; set { _isConnected = value; OnPropertyChanged(); RaiseAllCanExecuteChanged(); } }

    private bool _isTracking;
    public bool IsTracking { get => _isTracking; set { _isTracking = value; OnPropertyChanged(); RaiseAllCanExecuteChanged(); } }

    public MainViewModel()
    {
        TrajectoryPlotModel = BuildTrajectoryPlotModel();

        // Load persisted connection settings directly into the backing fields (not the property
        // setters) so restoring them doesn't immediately re-trigger a save.
        var (saved, token) = SettingsService.Load();
        _baseUrl = saved.BaseUrl;
        _authScheme = Enum.TryParse<I3xAuthScheme>(saved.AuthScheme, out var scheme) ? scheme : I3xAuthScheme.None;
        _apiKeyHeader = saved.ApiKeyHeader;
        _typeFilter = saved.TypeFilter;
        _token = token;
        _writeElementId = saved.WriteElementId;
        _writeCommand = CommandOptions.Contains(saved.WriteCommand) ? saved.WriteCommand : "STOP";
        _writeColor = ColorOptions.Contains(saved.WriteColor) ? saved.WriteColor : "NONE";
        _writePayload = BuildPayload(_writeCommand, _writeColor);

        _saveSettingsTimer = new DispatcherTimer { Interval = SaveDebounceDelay };
        _saveSettingsTimer.Tick += (_, _) => { _saveSettingsTimer.Stop(); PersistSettings(); };

        ConnectCommand = new RelayCommand(ConnectAsync);
        DiscoverCommand = new RelayCommand(DiscoverAsync, () => IsConnected);
        StartTrackingCommand = new RelayCommand(StartTrackingAsync, () => IsConnected && !IsTracking && Objects.Count > 0);
        StopTrackingCommand = new RelayCommand(() => { StopTracking(); return Task.CompletedTask; }, () => IsTracking);
        SendCommandCommand = new RelayCommand(SendCommandAsync,
            () => IsConnected && !string.IsNullOrWhiteSpace(WriteElementId) && !string.IsNullOrWhiteSpace(WritePayload));
        RefreshWriteTargetsCommand = new RelayCommand(LoadWriteTargetsAsync, () => IsConnected);
    }

    /// <summary>Debounces rapid successive edits (e.g. keystrokes in a textbox) into a single disk write.</summary>
    private void ScheduleSave()
    {
        _saveSettingsTimer.Stop();
        _saveSettingsTimer.Start();
    }

    private void PersistSettings()
    {
        var settings = new AppSettings
        {
            BaseUrl = BaseUrl,
            AuthScheme = AuthScheme.ToString(),
            ApiKeyHeader = ApiKeyHeader,
            TypeFilter = TypeFilter,
            WriteElementId = WriteElementId,
            WriteCommand = WriteCommand,
            WriteColor = WriteColor,
        };
        SettingsService.Save(settings, Token);
    }

    // Dark theme to match the rest of the UI (see MainWindow.xaml's palette).
    private static readonly OxyColor PlotBackground = OxyColor.FromRgb(0x25, 0x25, 0x26);
    private static readonly OxyColor PlotText = OxyColor.FromRgb(0xDC, 0xDC, 0xDC);
    private static readonly OxyColor PlotMutedText = OxyColor.FromRgb(0x9A, 0xA0, 0xA6);
    private static readonly OxyColor PlotBorder = OxyColor.FromRgb(0x3F, 0x3F, 0x46);
    private static readonly OxyColor PlotGridline = OxyColor.FromRgb(0x3A, 0x3A, 0x3D);

    private static PlotModel BuildTrajectoryPlotModel()
    {
        var model = new PlotModel
        {
            Title = "Trajectory (Y vs X)",
            Background = PlotBackground,
            PlotAreaBackground = PlotBackground,
            TextColor = PlotText,
            TitleColor = PlotText,
            PlotAreaBorderColor = PlotBorder,
        };
        model.Axes.Add(new LinearAxis
        {
            Key = "TrajX",
            Position = AxisPosition.Bottom,
            Title = "X",
            TextColor = PlotMutedText,
            TitleColor = PlotText,
            TicklineColor = PlotBorder,
            AxislineColor = PlotBorder,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = PlotGridline,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = PlotGridline,
        });
        model.Axes.Add(new LinearAxis
        {
            Key = "TrajY",
            Position = AxisPosition.Left,
            Title = "Y",
            TextColor = PlotMutedText,
            TitleColor = PlotText,
            TicklineColor = PlotBorder,
            AxislineColor = PlotBorder,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = PlotGridline,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = PlotGridline,
        });
        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.RightTop,
            LegendPlacement = LegendPlacement.Outside,
            LegendBackground = PlotBackground,
            LegendBorder = PlotBorder,
            LegendTextColor = PlotText,
            LegendTitleColor = PlotText,
        });
        return model;
    }

    private async Task ConnectAsync()
    {
        StopTracking();
        _client?.Dispose();
        Objects.Clear();
        _objectsById = new Dictionary<string, TrackedObject>();
        TrajectoryPlotModel.Series.Clear();
        TrajectoryPlotModel.InvalidatePlot(true);
        IsConnected = false;

        try
        {
            ConnectionStatus = "Connecting…";
            var client = new I3xClient(BaseUrl, AuthScheme, Token, ApiKeyHeader);
            var info = await client.GetInfoAsync().ConfigureAwait(true);
            _client = client;
            IsConnected = true;
            ConnectionStatus = $"Connected to {info.ServerName ?? BaseUrl} (spec {info.SpecVersion ?? "?"}).";
            await LoadWriteTargetsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatus = $"Connection failed: {ex.Message}";
        }
    }

    private async Task DiscoverAsync()
    {
        if (_client == null) return;
        try
        {
            ConnectionStatus = $"Discovering objects of type '{TypeFilter}'…";
            var found = await _client.GetObjectsAsync(TypeFilter).ConfigureAwait(true);

            StopTracking();
            Objects.Clear();
            TrajectoryPlotModel.Series.Clear();
            var byId = new Dictionary<string, TrackedObject>();

            var i = 0;
            foreach (var info in found.OrderBy(o => o.DisplayName ?? o.ElementId))
            {
                var color = Palette[i++ % Palette.Length];
                var tracked = new TrackedObject(info, color);
                tracked.TrackedChanged += (_, _) => ApplySeriesVisibility(tracked);
                Objects.Add(tracked);
                byId[tracked.ElementId] = tracked;
                TrajectoryPlotModel.Series.Add(tracked.TrajectorySeries);
                TrajectoryPlotModel.Series.Add(tracked.CurrentPositionSeries);
            }
            _objectsById = byId;

            TrajectoryPlotModel.InvalidatePlot(true);

            ConnectionStatus = found.Count == 0
                ? $"No objects found with type '{TypeFilter}'."
                : $"Found {found.Count} object(s) of type '{TypeFilter}'.";
            RaiseAllCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Discovery failed: {ex.Message}";
        }
    }

    /// <summary>Lists every object on the server (unfiltered) for the write-target picker. Best-effort.</summary>
    private async Task LoadWriteTargetsAsync()
    {
        if (_client == null) return;
        try
        {
            var all = await _client.GetObjectsAsync(null).ConfigureAwait(true);
            WriteTargets.Clear();
            foreach (var o in all.OrderBy(o => o.ElementId, StringComparer.OrdinalIgnoreCase))
                WriteTargets.Add(new WriteTarget(o.ElementId, o.DisplayName));
            WriteElementId = WriteElementId; // re-sync the picker with the current id
            WriteStatus = $"{WriteTargets.Count} object(s) available as write targets.";
        }
        catch (Exception ex)
        {
            WriteStatus = $"Couldn't list objects for the write picker ({ex.Message}); you can still type an Element Id.";
        }
    }

    private static string BuildPayload(string command, string color) =>
        JsonSerializer.Serialize(new { Command = command, Color = color });

    private void RebuildPayload() => WritePayload = BuildPayload(WriteCommand, WriteColor);

    private async Task SendCommandAsync()
    {
        if (_client == null) return;
        var elementId = WriteElementId.Trim();

        JsonElement value;
        try
        {
            using var doc = JsonDocument.Parse(WritePayload);
            value = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            WriteStatus = $"Payload is not valid JSON: {ex.Message}";
            return;
        }

        var compact = JsonSerializer.Serialize(value);
        try
        {
            WriteStatus = $"Writing to {elementId}…";
            await _client.WriteCurrentValueAsync(elementId, value).ConfigureAwait(true);
            WriteStatus = $"{DateTime.Now:HH:mm:ss}  Wrote {compact} to {elementId}.";
        }
        catch (Exception ex)
        {
            WriteStatus = $"{DateTime.Now:HH:mm:ss}  Write to {elementId} failed: {ex.Message}";
        }
    }

    private void ApplySeriesVisibility(TrackedObject obj)
    {
        obj.TrajectorySeries.IsVisible = obj.IsTracked;
        obj.CurrentPositionSeries.IsVisible = obj.IsTracked;
        TrajectoryPlotModel.InvalidatePlot(false);
    }

    /// <summary>
    /// Opens an i3X subscription for the discovered objects and starts a background loop that
    /// consumes the SSE event stream — no periodic re-fetching of current values.
    /// </summary>
    private async Task StartTrackingAsync()
    {
        if (_client == null || IsTracking) return;

        var client = _client;
        var elementIds = Objects.Select(o => o.ElementId).ToList();
        if (elementIds.Count == 0) return;

        try
        {
            ConnectionStatus = "Opening subscription…";
            var clientId = $"i3x-locations-tracker-{Guid.NewGuid():N}";
            var subscriptionId = await client.CreateSubscriptionAsync(clientId, "I3X Locations Tracker (WPF)").ConfigureAwait(true);
            await client.RegisterElementsAsync(clientId, subscriptionId, elementIds, maxDepth: 1).ConfigureAwait(true);

            _clientId = clientId;
            _subscriptionId = subscriptionId;
            _streamCts = new CancellationTokenSource();
            IsTracking = true;
            ConnectionStatus = "Streaming live…";

            _streamTask = Task.Run(() => RunStreamLoopAsync(client, clientId, subscriptionId, _streamCts.Token));
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Failed to start streaming: {ex.Message}";
            IsTracking = false;
        }
    }

    /// <summary>Runs on a background thread: reads the SSE stream and marshals each batch of updates to the UI thread.</summary>
    private async Task RunStreamLoopAsync(I3xClient client, string clientId, string subscriptionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var updates in client.StreamUpdatesAsync(clientId, subscriptionId, ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;
                    var batch = updates;
                    await _dispatcher.InvokeAsync(() => ApplyUpdates(batch));
                }

                if (ct.IsCancellationRequested) break;

                // Server closed the stream normally — reconnect rather than going silent.
                await _dispatcher.InvokeAsync(() => ConnectionStatus = "Stream closed by server; reconnecting…");
                await Task.Delay(StreamRetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                await _dispatcher.InvokeAsync(() => ConnectionStatus = $"Stream error: {ex.Message} — reconnecting…");
                try { await Task.Delay(StreamRetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Runs on the UI thread: applies one SSE event batch to the matching tracked objects.</summary>
    private void ApplyUpdates(List<SubscriptionUpdate> updates)
    {
        var anyChanged = false;
        foreach (var u in updates)
        {
            if (!_objectsById.TryGetValue(u.ElementId, out var obj)) continue;

            var readings = LocationsReading.ParseFrom(u.Value);
            if (readings.Count == 0)
            {
                obj.SetError("no X/Y data");
                continue;
            }
            if (obj.ApplyReadings(readings)) anyChanged = true;
        }

        if (anyChanged) TrajectoryPlotModel.InvalidatePlot(true);
    }

    private void StopTracking()
    {
        var wasTracking = IsTracking;
        IsTracking = false;

        _streamCts?.Cancel();
        _streamCts = null;
        _streamTask = null;

        if (wasTracking && _client != null && _clientId != null && _subscriptionId != null)
        {
            // Best-effort server-side cleanup; don't block the UI on it.
            _ = _client.DeleteSubscriptionAsync(_clientId, _subscriptionId);
            ConnectionStatus = "Stopped.";
        }
        _clientId = null;
        _subscriptionId = null;
    }

    private void RaiseAllCanExecuteChanged()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DiscoverCommand.RaiseCanExecuteChanged();
        StartTrackingCommand.RaiseCanExecuteChanged();
        StopTrackingCommand.RaiseCanExecuteChanged();
        SendCommandCommand.RaiseCanExecuteChanged();
        RefreshWriteTargetsCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        StopTracking();
        _client?.Dispose();

        if (_saveSettingsTimer.IsEnabled)
        {
            _saveSettingsTimer.Stop();
            PersistSettings();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>An object offered in the write-target picker.</summary>
public sealed record WriteTarget(string ElementId, string? DisplayName)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(DisplayName) || DisplayName == ElementId ? ElementId : $"{DisplayName}  —  {ElementId}";
}
