using System.ComponentModel;
using System.Runtime.CompilerServices;
using I3XLocationTracker.Models;
using OxyPlot;
using OxyPlot.Series;

namespace I3XLocationTracker.ViewModels;

/// <summary>
/// One discovered "Locations"-typed object: its live X/Y trajectory (path in the X-Y plane)
/// plus a marker for its current position, and the latest reading.
/// </summary>
public sealed class TrackedObject : INotifyPropertyChanged
{
    // Bound rolling window: drop points older than this so the trajectory doesn't grow unbounded.
    private static readonly TimeSpan RollingWindow = TimeSpan.FromMinutes(10);

    private readonly HashSet<long> _plottedTimestamps = new();
    private readonly List<DateTime> _pointTimestamps = new();

    public string ElementId { get; }
    public string DisplayName { get; }
    public OxyColor Color { get; }

    /// <summary>The path traced by (X, Y) over time.</summary>
    public LineSeries TrajectorySeries { get; }

    /// <summary>Single marker at the most recent (X, Y) position, so the current spot stands out from the trail.</summary>
    public ScatterSeries CurrentPositionSeries { get; }

    private bool _isTracked = true;
    public bool IsTracked
    {
        get => _isTracked;
        set { if (_isTracked != value) { _isTracked = value; OnPropertyChanged(); TrackedChanged?.Invoke(this, EventArgs.Empty); } }
    }

    private double _latestX;
    public double LatestX { get => _latestX; private set { _latestX = value; OnPropertyChanged(); } }

    private double _latestY;
    public double LatestY { get => _latestY; private set { _latestY = value; OnPropertyChanged(); } }

    private double? _latestBattery;
    public double? LatestBattery { get => _latestBattery; private set { _latestBattery = value; OnPropertyChanged(); } }

    private bool? _latestIsMoving;
    public bool? LatestIsMoving { get => _latestIsMoving; private set { _latestIsMoving = value; OnPropertyChanged(); } }

    private int? _latestSectorId;
    public int? LatestSectorId { get => _latestSectorId; private set { _latestSectorId = value; OnPropertyChanged(); } }

    private DateTime? _lastUpdateUtc;
    public DateTime? LastUpdateUtc { get => _lastUpdateUtc; private set { _lastUpdateUtc = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastUpdateLocal)); } }

    public DateTime? LastUpdateLocal => LastUpdateUtc?.ToLocalTime();

    private string _status = "waiting for data…";
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

    public event EventHandler? TrackedChanged;

    public TrackedObject(I3xObjectInfo info, OxyColor color)
    {
        ElementId = info.ElementId;
        DisplayName = string.IsNullOrWhiteSpace(info.DisplayName) ? info.ElementId : info.DisplayName!;
        Color = color;

        TrajectorySeries = new LineSeries
        {
            Title = DisplayName,
            Color = color,
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 2.5,
            MarkerFill = color,
            XAxisKey = "TrajX",
            YAxisKey = "TrajY",
        };
        CurrentPositionSeries = new ScatterSeries
        {
            MarkerType = MarkerType.Star,
            MarkerSize = 8,
            MarkerFill = color,
            MarkerStroke = OxyColors.White,
            MarkerStrokeThickness = 1,
            XAxisKey = "TrajX",
            YAxisKey = "TrajY",
            RenderInLegend = false,
        };
    }

    /// <summary>Appends new (X, Y) points to the trajectory (deduped by timestamp) and updates latest-value fields.</summary>
    public bool ApplyReadings(IEnumerable<LocationsReading> readings)
    {
        var applied = false;
        LocationsReading? newest = null;

        foreach (var r in readings.OrderBy(r => r.TimestampUtc))
        {
            var key = new DateTimeOffset(r.TimestampUtc).ToUnixTimeMilliseconds();
            if (!_plottedTimestamps.Add(key)) continue;

            TrajectorySeries.Points.Add(new DataPoint(r.X, r.Y));
            _pointTimestamps.Add(r.TimestampUtc);
            applied = true;
            newest = r;
        }

        if (applied) TrimOldPoints();

        if (newest != null)
        {
            LatestX = newest.X;
            LatestY = newest.Y;
            LatestBattery = newest.Battery;
            LatestIsMoving = newest.IsMoving;
            LatestSectorId = newest.SectorId;
            LastUpdateUtc = newest.TimestampUtc;
            Status = "live";

            CurrentPositionSeries.Points.Clear();
            CurrentPositionSeries.Points.Add(new ScatterPoint(newest.X, newest.Y));
        }

        return applied;
    }

    public void SetError(string message) => Status = message;

    private void TrimOldPoints()
    {
        if (TrajectorySeries.Points.Count == 0) return;
        var cutoff = DateTime.UtcNow - RollingWindow;
        while (_pointTimestamps.Count > 0 && _pointTimestamps[0] < cutoff)
        {
            _pointTimestamps.RemoveAt(0);
            TrajectorySeries.Points.RemoveAt(0);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
