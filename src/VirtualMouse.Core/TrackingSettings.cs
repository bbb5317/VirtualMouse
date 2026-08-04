namespace VirtualMouse.Core;

/// <summary>
/// Configuration settings for the tracking and gesture recognition pipeline.
/// All values are persisted to disk and restored on next launch.
/// </summary>
public class TrackingSettings
{
    // ── Camera ─────────────────────────────────────────────────────────────
    public int CameraDeviceIndex { get; set; } = 0;
    public int FrameWidth        { get; set; } = 1280;
    public int FrameHeight       { get; set; } = 800;
    public int TargetFps         { get; set; } = 120;

    // ── Camera Video Proc Amp (from ArduCam OV9281 driver panel) ──────────
    // These values match the user's configured driver settings.
    public double CamBrightness    { get; set; } = -64;
    public double CamContrast      { get; set; } = 64;
    public double CamSaturation    { get; set; } = 64;
    public double CamSharpness     { get; set; } = 3;
    public double CamGamma         { get; set; } = 72;
    public double CamWhiteBalance  { get; set; } = 4650;
    public double CamBacklightComp { get; set; } = 1;
    public double CamGain          { get; set; } = 0;

    // ── Detection ──────────────────────────────────────────────────────────

    /// <summary>Brightness threshold (0–255) for binary thresholding.</summary>
    public int BrightnessThreshold { get; set; } = 180;

    /// <summary>Minimum blob area in pixels.</summary>
    public double MinBlobArea { get; set; } = 4.0;

    /// <summary>Maximum blob area in pixels.</summary>
    public double MaxBlobArea { get; set; } = 800.0;

    // ── Shape Filter (Rectangular Markers) ────────────────────────────────

    /// <summary>
    /// Minimum rectangularity score (0–1) for a blob to be accepted as a marker.
    /// Rectangularity = blob_area / bounding_box_area.
    /// A filled rectangle scores ~0.9+. A circle scores ~0.78. A ring scores lower.
    /// The retroreflective tape markers are rectangular strips → high rectangularity.
    /// Keyboard key characters are irregular glyphs → lower rectangularity.
    /// Set to 0 to disable this filter.
    /// </summary>
    public double MinRectangularity { get; set; } = 0.55;

    /// <summary>
    /// Minimum aspect ratio of the bounding box (longer side / shorter side).
    /// Rectangular markers are elongated (ratio > 1.5).
    /// Round keyboard LEDs have ratio ~1.0.
    /// Set to 1.0 to disable.
    /// </summary>
    public double MinAspectRatio { get; set; } = 1.3;

    /// <summary>Maximum aspect ratio (prevents extremely thin noise lines).</summary>
    public double MaxAspectRatio { get; set; } = 8.0;

    // ── Motion Calibration ─────────────────────────────────────────────────

    /// <summary>
    /// Set of pixel positions (as "x,y" strings) that are known static blobs
    /// learned during the motion-calibration phase. Any blob whose centroid
    /// is within StaticBlacklistRadiusPx of a blacklisted position is rejected.
    /// </summary>
    public List<string> StaticBlacklist { get; set; } = new();

    /// <summary>
    /// Radius in pixels around a blacklisted position within which a blob
    /// is considered to be the same static object and is rejected.
    /// </summary>
    public double StaticBlacklistRadiusPx { get; set; } = 12.0;

    // ── Background Subtraction ─────────────────────────────────────────────
    public double BackgroundLearningRate { get; set; } = 0.005;

    // ── Grouping ───────────────────────────────────────────────────────────
    public double GroupingDistancePixels { get; set; } = 60.0;

    // ── Mouse Movement ─────────────────────────────────────────────────────
    public double MouseSensitivity { get; set; } = 2.5;
    public double DeadZonePixels   { get; set; } = 0.5;

    // ── Activation Gesture ─────────────────────────────────────────────────
    public double ActivationThresholdPixels  { get; set; } = -1;
    public double ActivationHysteresisPixels { get; set; } = 15.0;

    // ── Click Gestures ─────────────────────────────────────────────────────
    public int    TapMaxDurationMs      { get; set; } = 300;
    public double TapMinMovementPixels  { get; set; } = 8.0;
    public int    HoldThresholdMs       { get; set; } = 500;
}
