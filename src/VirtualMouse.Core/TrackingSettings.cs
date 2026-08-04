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

    /// <summary>
    /// Name fragment used to identify the camera device for USB reset.
    /// The resetter will match any device whose friendly name contains this string.
    /// Set to empty string to reset ALL camera-class devices.
    /// </summary>
    public string CameraResetNameFilter { get; set; } = "Arducam";

    // ── Camera Video Proc Amp ──────────────────────────────────────
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

    /// <summary>Minimum blob area in pixels (noise filter).</summary>
    public double MinBlobArea { get; set; } = 4.0;

    /// <summary>Maximum blob area in pixels (merged-blob filter).</summary>
    public double MaxBlobArea { get; set; } = 800.0;

    // ── Two-Speed Pipeline ─────────────────────────────────────────────────

    /// <summary>
    /// How often (in frames) the slow identification pass runs.
    /// At 120fps: 60 = every 0.5s, 120 = every 1s.
    /// Lower = faster re-identification but more CPU on the slow pass.
    /// </summary>
    public int IdentifyInterval { get; set; } = 60;

    /// <summary>
    /// Number of frames to collect before the first identification pass.
    /// During this period all blobs are shown but no gesture logic runs.
    /// </summary>
    public int IdentifyFrames { get; set; } = 60;

    /// <summary>
    /// Minimum displacement (pixels) a blob must show between two consecutive
    /// slow passes to be classified as a marker (moving object).
    /// Blobs that never move this far are classified as static (keyboard keys).
    /// Typical value: 6–15px. Set lower if hands move slowly; higher if
    /// camera vibration causes false positives.
    /// </summary>
    public double IdentifyMovementThresholdPx { get; set; } = 8.0;

    /// <summary>
    /// Scale factor for the fast per-frame pass (0.25–1.0).
    /// 0.5 = half resolution = ~4× faster per-frame processing.
    /// Centroid is up-scaled back to full-resolution coordinates.
    /// </summary>
    public double FastPassScale { get; set; } = 0.5;

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
