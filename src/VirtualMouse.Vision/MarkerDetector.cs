using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Detects retroreflective marker blobs using motion-based detection.
///
/// Two-stage motion detection:
///
///   Stage 1 — Frame Differencing (immediate, works from frame 2):
///     Computes the absolute difference between the current and previous frame.
///     Anything that changed between frames is a candidate. Fast and reliable
///     even without a warm-up period.
///
///   Stage 2 — MOG2 Background Subtraction (after warm-up):
///     Builds a statistical model of the static scene. After ~300 frames it
///     reliably separates moving objects from the static background.
///     More robust than frame differencing for slow-moving markers.
///
///   The two masks are OR-combined so a blob detected by either method passes.
///
/// PREREQUISITE: Auto-exposure (AGC) must be disabled on the camera.
/// With AGC active, every pixel shifts every frame and both methods fail.
/// CameraCapture.Open() disables AGC automatically.
/// </summary>
public class MarkerDetector : IDisposable
{
    private readonly ILogger<MarkerDetector> _logger;
    private readonly TrackingSettings _settings;

    // Reusable Mats
    private readonly Mat _grayFrame    = new();
    private readonly Mat _prevFrame    = new();
    private readonly Mat _diffMask     = new();
    private readonly Mat _fgMaskMog2   = new();
    private readonly Mat _combinedMask = new();
    private readonly Mat _maskedGray   = new();
    private readonly Mat _threshFrame  = new();

    private BackgroundSubtractorMOG2 _bgSub;
    private bool _hasPrevFrame;
    private int _framesSeen;

    // MOG2 is considered warmed up after this many frames
    private const int WarmUpFrames = 300;
    public bool IsWarmedUp => _framesSeen >= WarmUpFrames;

    public MarkerDetector(ILogger<MarkerDetector> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _bgSub = CreateBgSub();
    }

    private BackgroundSubtractorMOG2 CreateBgSub() =>
        BackgroundSubtractorMOG2.Create(
            history: 500,
            varThreshold: 60,   // raised significantly — OV9281 has per-frame noise
            detectShadows: false);

    public void ResetBackground()
    {
        _bgSub.Dispose();
        _bgSub = CreateBgSub();
        _hasPrevFrame = false;
        _framesSeen = 0;
        _logger.LogInformation("Background model reset.");
    }

    // ── Main Detection ─────────────────────────────────────────────────────

    public IReadOnlyList<MarkerBlob> Detect(Mat frame)
    {
        // Step 1: Grayscale
        if (frame.Channels() == 3)
            Cv2.CvtColor(frame, _grayFrame, ColorConversionCodes.BGR2GRAY);
        else
            frame.CopyTo(_grayFrame);

        // Step 2a: Frame differencing — works immediately from frame 2
        if (_hasPrevFrame)
        {
            Cv2.Absdiff(_grayFrame, _prevFrame, _diffMask);
            // Threshold the diff: only pixels that changed by more than ~15 counts
            Cv2.Threshold(_diffMask, _diffMask, 15, 255, ThresholdTypes.Binary);
            Cv2.Dilate(_diffMask, _diffMask, null, iterations: 4);
        }
        else
        {
            _diffMask.SetTo(Scalar.Black);
        }

        // Step 2b: MOG2 background subtraction — reliable after warm-up
        _bgSub.Apply(_grayFrame, _fgMaskMog2, learningRate: _settings.BackgroundLearningRate);
        Cv2.Dilate(_fgMaskMog2, _fgMaskMog2, null, iterations: 3);

        // Step 2c: Combine both masks — a blob detected by either method passes
        if (_hasPrevFrame)
            Cv2.BitwiseOr(_diffMask, _fgMaskMog2, _combinedMask);
        else
            _fgMaskMog2.CopyTo(_combinedMask);

        // Save current frame as previous
        _grayFrame.CopyTo(_prevFrame);
        _hasPrevFrame = true;
        _framesSeen++;

        // Step 3: Apply motion mask to grayscale frame
        Cv2.BitwiseAnd(_grayFrame, _grayFrame, _maskedGray, _combinedMask);

        // Step 4: Binary threshold — isolate bright moving blobs
        Cv2.Threshold(_maskedGray, _threshFrame,
            _settings.BrightnessThreshold, 255, ThresholdTypes.Binary);

        // Step 5: Connected components
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

            blobs.Add(new MarkerBlob
            {
                X = subX, Y = subY,
                Area = area,
                MeanIntensity = Cv2.Mean(roiGray, roiThresh).Val0
            });
        }

        return blobs;
    }

    // ── Debug Visualisation ────────────────────────────────────────────────

    public Mat DrawDebug(Mat frame, IReadOnlyList<MarkerBlob> blobs)
    {
        var debug = frame.Clone();
        if (debug.Channels() == 1)
            Cv2.CvtColor(debug, debug, ColorConversionCodes.GRAY2BGR);

        if (!IsWarmedUp)
        {
            Cv2.PutText(debug, $"Building background model... ({WarmUpFrames - _framesSeen} frames)",
                new Point(10, 30), HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 165, 255), 2);
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
        _grayFrame.Dispose(); _prevFrame.Dispose(); _diffMask.Dispose();
        _fgMaskMog2.Dispose(); _combinedMask.Dispose();
        _maskedGray.Dispose(); _threshFrame.Dispose();
        _bgSub.Dispose();
    }
}
