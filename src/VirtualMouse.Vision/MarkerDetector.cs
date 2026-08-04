using OpenCvSharp;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Vision;

/// <summary>
/// Processes a camera frame to detect reflective marker blobs.
/// 
/// Algorithm:
/// 1. Convert to grayscale (if not already).
/// 2. Apply binary threshold to isolate bright reflective markers.
/// 3. Find connected components (blobs).
/// 4. Filter blobs by area.
/// 5. Calculate intensity-weighted centroid (image moments) for sub-pixel precision.
/// </summary>
public class MarkerDetector
{
    private readonly ILogger<MarkerDetector> _logger;
    private readonly TrackingSettings _settings;

    // Reusable Mat objects to reduce GC pressure in the hot path
    private readonly Mat _grayFrame = new();
    private readonly Mat _threshFrame = new();

    public MarkerDetector(ILogger<MarkerDetector> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Detects all reflective marker blobs in the given frame.
    /// </summary>
    /// <param name="frame">A BGR or grayscale Mat from the camera.</param>
    /// <returns>A list of detected MarkerBlob objects with sub-pixel centroid coordinates.</returns>
    public IReadOnlyList<MarkerBlob> Detect(Mat frame)
    {
        // Step 1: Ensure we have a grayscale image
        if (frame.Channels() == 3)
            Cv2.CvtColor(frame, _grayFrame, ColorConversionCodes.BGR2GRAY);
        else
            frame.CopyTo(_grayFrame);

        // Step 2: Binary threshold - isolate bright retro-reflective markers
        Cv2.Threshold(_grayFrame, _threshFrame, _settings.BrightnessThreshold, 255, ThresholdTypes.Binary);

        // Step 3: Find connected components
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int numLabels = Cv2.ConnectedComponentsWithStats(
            _threshFrame, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);

        var blobs = new List<MarkerBlob>();

        // Label 0 is the background; start from 1
        for (int label = 1; label < numLabels; label++)
        {
            double area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);

            // Step 4: Filter by area
            if (area < _settings.MinBlobArea || area > _settings.MaxBlobArea)
                continue;

            // Step 5: Sub-pixel centroid via image moments on the grayscale ROI
            int blobX = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
            int blobY = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
            int blobW = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            int blobH = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);

            // Clamp ROI to frame bounds
            var roi = new Rect(
                Math.Max(0, blobX),
                Math.Max(0, blobY),
                Math.Min(blobW, frame.Width  - blobX),
                Math.Min(blobH, frame.Height - blobY));

            if (roi.Width <= 0 || roi.Height <= 0) continue;

            using var roiGray = new Mat(_grayFrame, roi);
            using var roiMask = new Mat(_threshFrame, roi);

            // Compute moments only within the thresholded mask region
            using var maskedRoi = new Mat();
            Cv2.BitwiseAnd(roiGray, roiMask, maskedRoi);
            var moments = Cv2.Moments(maskedRoi, binaryImage: false);

            double subPixelX, subPixelY;
            if (moments.M00 > 0)
            {
                // Intensity-weighted centroid (sub-pixel precision)
                subPixelX = blobX + (moments.M10 / moments.M00);
                subPixelY = blobY + (moments.M01 / moments.M00);
            }
            else
            {
                // Fallback to connected components centroid
                subPixelX = centroids.At<double>(label, 0);
                subPixelY = centroids.At<double>(label, 1);
            }

            // Calculate mean intensity for quality scoring
            var meanIntensity = Cv2.Mean(roiGray, roiMask).Val0;

            blobs.Add(new MarkerBlob
            {
                X = subPixelX,
                Y = subPixelY,
                Area = area,
                MeanIntensity = meanIntensity
            });
        }

        _logger.LogDebug("Detected {Count} marker blobs in frame.", blobs.Count);
        return blobs;
    }

    /// <summary>
    /// Draws detected blobs onto a debug visualization frame.
    /// </summary>
    public Mat DrawDebug(Mat frame, IReadOnlyList<MarkerBlob> blobs)
    {
        var debug = frame.Clone();
        if (debug.Channels() == 1)
            Cv2.CvtColor(debug, debug, ColorConversionCodes.GRAY2BGR);

        foreach (var blob in blobs)
        {
            var center = new Point((int)blob.X, (int)blob.Y);
            Cv2.Circle(debug, center, 8, Scalar.Green, 2);
            Cv2.Circle(debug, center, 1, Scalar.Red, -1);
            Cv2.PutText(debug, $"({blob.X:F1},{blob.Y:F1})",
                new Point(center.X + 10, center.Y),
                HersheyFonts.HersheySimplex, 0.4, Scalar.Yellow, 1);
        }

        return debug;
    }
}
