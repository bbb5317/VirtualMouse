using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Two-speed detection pipeline:
///
///   SLOW PASS — runs every N frames (default every 60 frames, ~0.5s at 120fps)
///   ─────────────────────────────────────────────────────────────────────────
///   Scans the full-resolution frame for ALL bright blobs above threshold.
///   For each blob, tracks how far its centroid has moved since the last slow pass.
///   A blob that has moved more than IdentifyMovementThresholdPx is a MARKER.
///   A blob that has not moved is STATIC (keyboard key, reflection) and ignored.
///   The set of identified marker positions is updated atomically.
///   This runs on the capture thread but does minimal work (no gesture logic).
///
///   FAST PASS — runs on every frame
///   ─────────────────────────────────────────────────────────────────────────
///   Downscales the frame to FastPassScale (default 0.5 = half resolution).
///   Searches only in a small window around each known marker position.
///   Returns the refined centroid of each marker at sub-pixel accuracy.
///   This is the only pass that feeds into gesture recognition.
///
///   NET RESULT:
///   - Static keyboard keys are automatically excluded without any user action.
///   - The system self-corrects every N frames as hands move around.
///   - Per-frame CPU load is proportional to the number of markers (≤10),
///     not the number of all bright blobs in the frame.
/// </summary>
public class MarkerDetector : IDisposable
{
    private readonly ILogger<MarkerDetector> _logger;
    private readonly TrackingSettings _settings;

    // Reusable Mats
    private readonly Mat _grayFull  = new();
    private readonly Mat _threshFull = new();
    private readonly Mat _graySmall = new();

    // ── Slow pass state ────────────────────────────────────────────────────

    private int _framesSeen;

    // Previous slow-pass blob centroids: used to measure displacement
    private List<(double X, double Y)> _prevSlowBlobs = new();

    // Displacement accumulator: key = grid-snapped "x,y", value = max displacement seen
    private readonly Dictionary<string, double> _blobDisplacement = new();

    // The current set of confirmed marker positions (updated atomically)
    // Each entry is the centroid from the last slow pass.
    private volatile MarkerPosition[] _confirmedMarkers = Array.Empty<MarkerPosition>();

    // How many slow passes have completed (for UI feedback)
    public int SlowPassCount { get; private set; }
    public int ConfirmedMarkerCount => _confirmedMarkers.Length;

    // ── Fast pass state ────────────────────────────────────────────────────

    private const int FastWindowRadius = 30; // px in full-res coords to search around each marker

    // Minimum number of slow passes required before identification is considered complete.
    // We need at least 2 passes to measure displacement (pass 1 = baseline, pass 2 = compare).
    private const int MinSlowPassesRequired = 3;

    // ── Public state ───────────────────────────────────────────────────────

    /// <summary>
    /// True while the system is still learning which blobs are markers.
    /// Requires at least MinSlowPassesRequired slow passes AND at least one
    /// confirmed marker to be found before identification is considered done.
    /// </summary>
    public bool IsIdentifying => SlowPassCount < MinSlowPassesRequired || _confirmedMarkers.Length == 0;

    // ── Constructor ────────────────────────────────────────────────────────

    public MarkerDetector(ILogger<MarkerDetector> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>Resets the identification state so the slow pass re-learns from scratch.</summary>
    public void ResetIdentification()
    {
        _prevSlowBlobs.Clear();
        _blobDisplacement.Clear();
        _confirmedMarkers = Array.Empty<MarkerPosition>();
        _framesSeen = 0;
        SlowPassCount = 0;
        _logger.LogInformation("Marker identification reset.");
    }

    // ── Main entry point ───────────────────────────────────────────────────

    public IReadOnlyList<MarkerBlob> Detect(Mat frame)
    {
        _framesSeen++;

        // Step 1: Grayscale (full resolution)
        if (frame.Channels() == 3)
            Cv2.CvtColor(frame, _grayFull, ColorConversionCodes.BGR2GRAY);
        else
            frame.CopyTo(_grayFull);

        // Step 2: Slow pass — runs every IdentifyInterval frames
        if (_framesSeen % _settings.IdentifyInterval == 0)
            RunSlowPass();

        // Step 3: Fast pass — runs every frame using confirmed marker positions
        return RunFastPass(frame);
    }

    // ── Slow Pass ──────────────────────────────────────────────────────────

    private void RunSlowPass()
    {
        // Threshold the full-res frame to find ALL bright blobs
        Cv2.Threshold(_grayFull, _threshFull,
            _settings.BrightnessThreshold, 255, ThresholdTypes.Binary);

        using var labels    = new Mat();
        using var stats     = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(
            _threshFull, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);

        var currentBlobs = new List<(double X, double Y)>();

        for (int lbl = 1; lbl < n; lbl++)
        {
            double area = stats.At<int>(lbl, (int)ConnectedComponentsTypes.Area);
            if (area < _settings.MinBlobArea || area > _settings.MaxBlobArea) continue;

            double cx = centroids.At<double>(lbl, 0);
            double cy = centroids.At<double>(lbl, 1);
            currentBlobs.Add((cx, cy));

            // Grid-snap to 8px for stable key
            string key = $"{(int)(cx / 8) * 8},{(int)(cy / 8) * 8}";

            if (!_blobDisplacement.ContainsKey(key))
                _blobDisplacement[key] = 0;

            // Measure displacement from closest previous blob
            if (_prevSlowBlobs.Count > 0)
            {
                double minDist = _prevSlowBlobs
                    .Select(p => Math.Sqrt(Math.Pow(cx - p.X, 2) + Math.Pow(cy - p.Y, 2)))
                    .Min();
                if (minDist > _blobDisplacement[key])
                    _blobDisplacement[key] = minDist;
            }
        }

        _prevSlowBlobs = currentBlobs;

        // Promote blobs that have moved enough to confirmed markers
        var confirmed = new List<MarkerPosition>();
        foreach (var (key, disp) in _blobDisplacement)
        {
            if (disp >= _settings.IdentifyMovementThresholdPx)
            {
                var parts = key.Split(',');
                confirmed.Add(new MarkerPosition(double.Parse(parts[0]), double.Parse(parts[1])));
            }
        }

        _confirmedMarkers = confirmed.ToArray();
        SlowPassCount++;

        _logger.LogDebug(
            "Slow pass #{P}: {Total} blobs, {Confirmed} confirmed markers.",
            SlowPassCount, currentBlobs.Count, confirmed.Count);
    }

    // ── Fast Pass ──────────────────────────────────────────────────────────

    private IReadOnlyList<MarkerBlob> RunFastPass(Mat frame)
    {
        var markers = _confirmedMarkers; // snapshot (atomic reference read)
        if (markers.Length == 0)
            return Array.Empty<MarkerBlob>();

        // Downscale for speed
        double scale = _settings.FastPassScale;
        int sw = (int)(frame.Width  * scale);
        int sh = (int)(frame.Height * scale);
        Cv2.Resize(_grayFull, _graySmall, new OpenCvSharp.Size(sw, sh), interpolation: InterpolationFlags.Area);

        var blobs = new List<MarkerBlob>(markers.Length);

        foreach (var marker in markers)
        {
            // Search window around the known marker position (scaled)
            int cx = (int)(marker.X * scale);
            int cy = (int)(marker.Y * scale);
            int r  = (int)(FastWindowRadius * scale);

            var roi = new Rect(
                Math.Max(0, cx - r), Math.Max(0, cy - r),
                Math.Min(2 * r, sw - Math.Max(0, cx - r)),
                Math.Min(2 * r, sh - Math.Max(0, cy - r)));
            if (roi.Width <= 0 || roi.Height <= 0) continue;

            using var roiGray = new Mat(_graySmall, roi);
            using var roiThresh = new Mat();
            Cv2.Threshold(roiGray, roiThresh, _settings.BrightnessThreshold, 255, ThresholdTypes.Binary);

            var moments = Cv2.Moments(roiThresh, binaryImage: false);
            if (moments.M00 <= 0) continue;

            // Sub-pixel centroid in full-resolution coordinates
            double subX = (roi.X + moments.M10 / moments.M00) / scale;
            double subY = (roi.Y + moments.M01 / moments.M00) / scale;

            blobs.Add(new MarkerBlob
            {
                X = subX, Y = subY,
                Area = moments.M00 / (scale * scale),
                MeanIntensity = Cv2.Mean(roiGray, roiThresh).Val0
            });
        }

        return blobs;
    }

    // ── Debug Visualisation ────────────────────────────────────────────────

    public Mat DrawDebug(Mat frame, IReadOnlyList<MarkerBlob> blobs,
        double virtualCursorX = -1, double virtualCursorY = -1)
    {
        var debug = frame.Clone();
        if (debug.Channels() == 1)
            Cv2.CvtColor(debug, debug, ColorConversionCodes.GRAY2BGR);

        if (IsIdentifying)
        {
            // During identification: show ALL bright blobs from the last slow pass
            // so the user can see what the camera sees and verify the threshold.
            string idStatus = $"Identifying... pass {SlowPassCount} | {_prevSlowBlobs.Count} blobs seen";
            Cv2.PutText(debug, idStatus, new Point(10, 28),
                HersheyFonts.HersheySimplex, 0.60, new Scalar(0, 140, 255), 2);

            // Draw all blobs seen in the last slow pass as yellow circles
            foreach (var (bx, by) in _prevSlowBlobs)
            {
                var c = new Point((int)bx, (int)by);
                Cv2.Circle(debug, c, 6, new Scalar(0, 220, 255), 1);
            }

            // Draw confirmed markers so far as bright green
            foreach (var m in _confirmedMarkers)
            {
                var c = new Point((int)m.X, (int)m.Y);
                Cv2.Circle(debug, c, 10, new Scalar(0, 255, 80), 2);
                Cv2.Circle(debug, c, 3,  new Scalar(0, 255, 80), -1);
            }
        }
        else
        {
            // After identification: show confirmed marker search windows and tracked blobs
            string trackStatus = $"Tracking {ConfirmedMarkerCount} markers";
            Cv2.PutText(debug, trackStatus, new Point(10, 28),
                HersheyFonts.HersheySimplex, 0.65, new Scalar(0, 220, 0), 2);

            // Draw confirmed marker search windows (orange boxes)
            foreach (var m in _confirmedMarkers)
            {
                Cv2.Rectangle(debug,
                    new Point((int)(m.X - FastWindowRadius), (int)(m.Y - FastWindowRadius)),
                    new Point((int)(m.X + FastWindowRadius), (int)(m.Y + FastWindowRadius)),
                    new Scalar(0, 140, 255), 1);
            }

            // Draw tracked blobs (green circles with red centre dot)
            foreach (var blob in blobs)
            {
                var c = new Point((int)blob.X, (int)blob.Y);
                Cv2.Circle(debug, c, 8, Scalar.Green, 2);
                Cv2.Circle(debug, c, 2, Scalar.Red, -1);
            }
        }

        // Draw virtual cursor crosshair in test mode (when virtualCursorX >= 0)
        if (virtualCursorX >= 0 && virtualCursorY >= 0)
        {
            int cx = (int)virtualCursorX;
            int cy = (int)virtualCursorY;
            int r  = 14;
            // White outer ring
            Cv2.Circle(debug, new Point(cx, cy), r, new Scalar(255, 255, 255), 2);
            // Cyan inner dot
            Cv2.Circle(debug, new Point(cx, cy), 4, new Scalar(255, 220, 0), -1);
            // Crosshair lines
            Cv2.Line(debug, new Point(cx - r - 6, cy), new Point(cx - r + 2, cy), new Scalar(255, 255, 255), 1);
            Cv2.Line(debug, new Point(cx + r - 2, cy), new Point(cx + r + 6, cy), new Scalar(255, 255, 255), 1);
            Cv2.Line(debug, new Point(cx, cy - r - 6), new Point(cx, cy - r + 2), new Scalar(255, 255, 255), 1);
            Cv2.Line(debug, new Point(cx, cy + r - 2), new Point(cx, cy + r + 6), new Scalar(255, 255, 255), 1);
            // TEST MODE label
            Cv2.PutText(debug, "TEST MODE", new Point(10, debug.Height - 12),
                HersheyFonts.HersheySimplex, 0.55, new Scalar(0, 184, 255), 2);
        }

        return debug;
    }

    public void Dispose()
    {
        _grayFull.Dispose();
        _threshFull.Dispose();
        _graySmall.Dispose();
    }

    // ── Inner types ────────────────────────────────────────────────────────

    private record MarkerPosition(double X, double Y);
}
