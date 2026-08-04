using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Detects retroreflective marker blobs using motion-based background subtraction.
///
/// The core insight: keyboard key characters are static — they never move.
/// Retroreflective finger markers always move. Therefore, a background subtraction
/// model (MOG2) that learns the static scene will pass only the moving markers
/// and reject everything static, regardless of brightness or shape.
///
/// Pipeline:
///   1. Convert frame to grayscale.
///   2. Apply MOG2 background subtractor → foreground mask (moving pixels only).
///   3. Dilate the mask slightly to fill small gaps within a marker.
///   4. Mask the original grayscale frame with the foreground mask.
///   5. Binary threshold on the masked frame to isolate bright moving blobs.
///   6. Connected components → filter by area → sub-pixel centroid via moments.
/// </summary>
public class MarkerDetector : IDisposable
{
    private readonly ILogger<MarkerDetector> _logger;
    private readonly TrackingSettings _settings;

    // Reusable Mats — allocated once to avoid per-frame GC pressure
    private readonly Mat _grayFrame   = new();
    private readonly Mat _fgMask      = new();
    private readonly Mat _maskedGray  = new();
    private readonly Mat _threshFrame = new();

    // MOG2 background subtractor
    private BackgroundSubtractorMOG2 _bgSub;

    // Warm-up: the background model needs a few seconds of frames before it is
    // reliable. During warm-up we show blobs but do not inject mouse events.
    private int _framesSeen;
    private const int WarmUpFrames = 60; // ~0.5s at 120fps

    public bool IsWarmedUp => _framesSeen >= WarmUpFrames;

    public MarkerDetector(ILogger<MarkerDetector> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _bgSub = CreateBgSub();
    }

    private BackgroundSubtractorMOG2 CreateBgSub() =>
        BackgroundSubtractorMOG2.Create(
            history: 300,          // frames to build the model (~2.5s at 120fps)
            varThreshold: 25,      // sensitivity — lower = more sensitive to change
            detectShadows: false); // shadows are irrelevant for bright markers

    /// <summary>
    /// Resets the background model. Call this when the camera is repositioned
    /// or the scene changes significantly (e.g. lights turned on/off).
    /// </summary>
    public void ResetBackground()
    {
        _bgSub.Dispose();
        _bgSub = CreateBgSub();
        _framesSeen = 0;
        _logger.LogInformation("Background model reset. Warming up...");
    }

    // ── Main Detection ─────────────────────────────────────────────────────

    public IReadOnlyList<MarkerBlob> Detect(Mat frame)
    {
        // Step 1: Grayscale
        if (frame.Channels() == 3)
            Cv2.CvtColor(frame, _grayFrame, ColorConversionCodes.BGR2GRAY);
        else
            frame.CopyTo(_grayFrame);

        // Step 2: Background subtraction — produces a binary foreground mask
        // learningRate: small positive value so the model slowly adapts to
        // gradual lighting changes without immediately absorbing moving markers.
        _bgSub.Apply(_grayFrame, _fgMask, learningRate: _settings.BackgroundLearningRate);
        _framesSeen++;

        // Step 3: Dilate the foreground mask to fill small intra-marker gaps
        // (the centre of a retroreflective marker can sometimes be slightly
        // darker than its edges, creating a ring rather than a filled blob).
        Cv2.Dilate(_fgMask, _fgMask, null, iterations: 3);

        // Step 4: Mask the grayscale frame — only moving pixels remain
        Cv2.BitwiseAnd(_grayFrame, _grayFrame, _maskedGray, _fgMask);

        // Step 5: Binary threshold — isolate bright moving blobs
        Cv2.Threshold(_maskedGray, _threshFrame,
            _settings.BrightnessThreshold, 255, ThresholdTypes.Binary);

        // Step 6: Connected components
        using var labels    = new Mat();
        using var stats     = new Mat();
        using var centroids = new Mat();
        int numLabels = Cv2.ConnectedComponentsWithStats(
            _threshFrame, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);

        var blobs = new List<MarkerBlob>();

        for (int label = 1; label < numLabels; label++)
        {
            double area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area < _settings.MinBlobArea || area > _settings.MaxBlobArea) continue;

            int bx = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
            int by = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
            int bw = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            int bh = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);

            // Step 7: Sub-pixel centroid via intensity-weighted image moments
            var roi = new Rect(
                Math.Max(0, bx), Math.Max(0, by),
                Math.Min(bw, frame.Width  - bx),
                Math.Min(bh, frame.Height - by));
            if (roi.Width <= 0 || roi.Height <= 0) continue;

            using var roiGray  = new Mat(_grayFrame,   roi);
            using var roiThresh = new Mat(_threshFrame, roi);
            using var masked    = new Mat();
            Cv2.BitwiseAnd(roiGray, roiThresh, masked);
            var moments = Cv2.Moments(masked, binaryImage: false);

            double subX, subY;
            if (moments.M00 > 0)
            {
                subX = bx + moments.M10 / moments.M00;
                subY = by + moments.M01 / moments.M00;
            }
            else
            {
                subX = centroids.At<double>(label, 0);
                subY = centroids.At<double>(label, 1);
            }

            var meanIntensity = Cv2.Mean(roiGray, roiThresh).Val0;

            blobs.Add(new MarkerBlob
            {
                X = subX, Y = subY,
                Area = area,
                MeanIntensity = meanIntensity
            });
        }

        _logger.LogDebug("Frame {F}: {C} blob(s) detected (warmed up: {W}).",
            _framesSeen, blobs.Count, IsWarmedUp);
        return blobs;
    }

    // ── Debug Visualisation ────────────────────────────────────────────────

    public Mat DrawDebug(Mat frame, IReadOnlyList<MarkerBlob> blobs)
    {
        var debug = frame.Clone();
        if (debug.Channels() == 1)
            Cv2.CvtColor(debug, debug, ColorConversionCodes.GRAY2BGR);

        // Warm-up overlay
        if (!IsWarmedUp)
        {
            int remaining = WarmUpFrames - _framesSeen;
            Cv2.PutText(debug,
                $"Learning background... ({remaining} frames)",
                new Point(10, 30),
                HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 165, 255), 2);
        }

        foreach (var blob in blobs)
        {
            var center = new Point((int)blob.X, (int)blob.Y);
            Cv2.Circle(debug, center, 8, Scalar.Green, 2);
            Cv2.Circle(debug, center, 2, Scalar.Red, -1);
            Cv2.PutText(debug, $"({blob.X:F0},{blob.Y:F0})",
                new Point(center.X + 10, center.Y),
                HersheyFonts.HersheySimplex, 0.35, Scalar.Yellow, 1);
        }

        return debug;
    }

    public void Dispose()
    {
        _grayFrame.Dispose();
        _fgMask.Dispose();
        _maskedGray.Dispose();
        _threshFrame.Dispose();
        _bgSub.Dispose();
    }
}
