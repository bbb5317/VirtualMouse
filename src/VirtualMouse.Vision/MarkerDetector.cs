using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Detects retroreflective marker blobs using a three-layer pipeline:
///
///   Layer 1 — Brightness threshold:
///     Binary threshold isolates all bright blobs in the frame.
///
///   Layer 2 — Rectangular shape filter:
///     The retroreflective tape markers are rectangular strips.
///     Keyboard key characters are irregular glyphs; round LEDs are circular.
///     Filter by: rectangularity (blob area / bounding box area) and aspect ratio.
///
///   Layer 3 — Static blacklist (motion calibration):
///     During a calibration phase the user waves their hands. Any blob that
///     moves more than a threshold distance is registered as a marker candidate.
///     Any blob that never moves is added to a static blacklist and rejected
///     in all subsequent frames. This permanently removes keyboard keys,
///     desk reflections, and other static bright objects.
/// </summary>
public class MarkerDetector : IDisposable
{
    private readonly ILogger<MarkerDetector> _logger;
    private readonly TrackingSettings _settings;

    // Reusable Mats
    private readonly Mat _grayFrame   = new();
    private readonly Mat _threshFrame = new();

    // ── Motion Calibration State ───────────────────────────────────────────

    public enum CalibrationState { Idle, Recording, Complete }
    public CalibrationState State { get; private set; } = CalibrationState.Idle;

    // During recording: track each blob's max displacement from its first seen position
    // Key = "x,y" of first-seen centroid (rounded), Value = max displacement seen
    private readonly Dictionary<string, double> _blobMaxDisplacement = new();
    // All centroids seen in the current recording frame
    private readonly List<(double X, double Y)> _prevCalibCentroids = new();

    // Blacklist loaded from settings (static blobs to always reject)
    private List<(double X, double Y)> _blacklist = new();

    // Warm-up: suppress output for the first N frames so AGC can stabilise
    private int _framesSeen;
    private const int WarmUpFrames = 30;
    public bool IsWarmedUp => _framesSeen >= WarmUpFrames;

    public MarkerDetector(ILogger<MarkerDetector> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
        LoadBlacklist();
    }

    private void LoadBlacklist()
    {
        _blacklist = _settings.StaticBlacklist
            .Select(s => s.Split(','))
            .Where(p => p.Length == 2)
            .Select(p => (double.Parse(p[0]), double.Parse(p[1])))
            .ToList();
        _logger.LogInformation("Loaded {Count} blacklisted positions.", _blacklist.Count);
    }

    // ── Calibration Control ────────────────────────────────────────────────

    /// <summary>Start recording blob positions for motion calibration.</summary>
    public void StartCalibration()
    {
        _blobMaxDisplacement.Clear();
        _prevCalibCentroids.Clear();
        State = CalibrationState.Recording;
        _logger.LogInformation("Motion calibration started — wave your hands.");
    }

    /// <summary>
    /// Stop recording and build the static blacklist.
    /// Any blob that never moved more than MinMovementPx is static → blacklisted.
    /// </summary>
    public void StopCalibration(double minMovementPx = 8.0)
    {
        var newBlacklist = new List<string>();
        foreach (var (key, maxDisp) in _blobMaxDisplacement)
        {
            if (maxDisp < minMovementPx)
                newBlacklist.Add(key);
        }

        _settings.StaticBlacklist = newBlacklist;
        LoadBlacklist();
        State = CalibrationState.Complete;
        _logger.LogInformation(
            "Calibration complete. {Static} static blobs blacklisted, {Moving} moving blobs kept.",
            newBlacklist.Count,
            _blobMaxDisplacement.Count - newBlacklist.Count);
    }

    public void ClearBlacklist()
    {
        _settings.StaticBlacklist.Clear();
        _blacklist.Clear();
        State = CalibrationState.Idle;
        _logger.LogInformation("Static blacklist cleared.");
    }

    // ── Main Detection ─────────────────────────────────────────────────────

    public IReadOnlyList<MarkerBlob> Detect(Mat frame)
    {
        _framesSeen++;

        // Step 1: Grayscale
        if (frame.Channels() == 3)
            Cv2.CvtColor(frame, _grayFrame, ColorConversionCodes.BGR2GRAY);
        else
            frame.CopyTo(_grayFrame);

        // Step 2: Binary threshold — isolate all bright blobs
        Cv2.Threshold(_grayFrame, _threshFrame,
            _settings.BrightnessThreshold, 255, ThresholdTypes.Binary);

        // Step 3: Connected components
        using var labels    = new Mat();
        using var stats     = new Mat();
        using var centroids = new Mat();
        int numLabels = Cv2.ConnectedComponentsWithStats(
            _threshFrame, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);

        var candidates = new List<MarkerBlob>();

        for (int label = 1; label < numLabels; label++)
        {
            double area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area < _settings.MinBlobArea || area > _settings.MaxBlobArea) continue;

            int bx = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
            int by = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
            int bw = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            int bh = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            if (bw <= 0 || bh <= 0) continue;

            // ── Layer 2: Rectangular shape filter ─────────────────────────

            // Rectangularity: how much of the bounding box is filled
            double rectangularity = area / (double)(bw * bh);
            if (rectangularity < _settings.MinRectangularity) continue;

            // Aspect ratio: longer side / shorter side
            double aspectRatio = bw >= bh
                ? (double)bw / bh
                : (double)bh / bw;
            if (aspectRatio < _settings.MinAspectRatio || aspectRatio > _settings.MaxAspectRatio) continue;

            // ── Sub-pixel centroid via intensity-weighted moments ──────────
            var roi = new Rect(
                Math.Max(0, bx), Math.Max(0, by),
                Math.Min(bw, frame.Width  - bx),
                Math.Min(bh, frame.Height - by));
            if (roi.Width <= 0 || roi.Height <= 0) continue;

            using var roiGray   = new Mat(_grayFrame,   roi);
            using var roiThresh = new Mat(_threshFrame, roi);
            using var masked    = new Mat();
            Cv2.BitwiseAnd(roiGray, roiThresh, masked);
            var moments = Cv2.Moments(masked, binaryImage: false);

            double subX = moments.M00 > 0 ? bx + moments.M10 / moments.M00 : centroids.At<double>(label, 0);
            double subY = moments.M00 > 0 ? by + moments.M01 / moments.M00 : centroids.At<double>(label, 1);

            // ── Layer 3: Static blacklist check ───────────────────────────
            if (IsBlacklisted(subX, subY)) continue;

            candidates.Add(new MarkerBlob
            {
                X = subX, Y = subY,
                Area = area,
                MeanIntensity = Cv2.Mean(roiGray, roiThresh).Val0
            });
        }

        // ── Update calibration recording ───────────────────────────────────
        if (State == CalibrationState.Recording)
            UpdateCalibration(candidates);

        return candidates;
    }

    // ── Calibration Tracking ───────────────────────────────────────────────

    private void UpdateCalibration(List<MarkerBlob> blobs)
    {
        foreach (var blob in blobs)
        {
            // Round to nearest 5px for stable key generation
            string key = $"{Math.Round(blob.X / 5) * 5},{Math.Round(blob.Y / 5) * 5}";

            if (!_blobMaxDisplacement.ContainsKey(key))
                _blobMaxDisplacement[key] = 0;

            // Find closest previous centroid and measure displacement
            double maxDisp = _blobMaxDisplacement[key];
            foreach (var prev in _prevCalibCentroids)
            {
                double dist = Math.Sqrt(Math.Pow(blob.X - prev.X, 2) + Math.Pow(blob.Y - prev.Y, 2));
                if (dist > maxDisp) maxDisp = dist;
            }
            _blobMaxDisplacement[key] = maxDisp;
        }

        _prevCalibCentroids.Clear();
        _prevCalibCentroids.AddRange(blobs.Select(b => (b.X, b.Y)));
    }

    private bool IsBlacklisted(double x, double y)
    {
        double r = _settings.StaticBlacklistRadiusPx;
        foreach (var (bx, by) in _blacklist)
        {
            if (Math.Abs(x - bx) < r && Math.Abs(y - by) < r)
                return true;
        }
        return false;
    }

    // ── Debug Visualisation ────────────────────────────────────────────────

    public Mat DrawDebug(Mat frame, IReadOnlyList<MarkerBlob> blobs)
    {
        var debug = frame.Clone();
        if (debug.Channels() == 1)
            Cv2.CvtColor(debug, debug, ColorConversionCodes.GRAY2BGR);

        if (!IsWarmedUp)
        {
            Cv2.PutText(debug, $"Stabilising... ({WarmUpFrames - _framesSeen} frames)",
                new Point(10, 30), HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 165, 255), 2);
        }
        else if (State == CalibrationState.Recording)
        {
            Cv2.PutText(debug, "CALIBRATING — wave your hands!",
                new Point(10, 30), HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 80, 255), 2);
        }

        foreach (var blob in blobs)
        {
            var c = new Point((int)blob.X, (int)blob.Y);
            Cv2.Circle(debug, c, 8, Scalar.Green, 2);
            Cv2.Circle(debug, c, 2, Scalar.Red, -1);
            Cv2.PutText(debug, $"({blob.X:F0},{blob.Y:F0})",
                new Point(c.X + 10, c.Y), HersheyFonts.HersheySimplex, 0.35, Scalar.Yellow, 1);
        }
        return debug;
    }

    public void Dispose()
    {
        _grayFrame.Dispose();
        _threshFrame.Dispose();
    }
}
