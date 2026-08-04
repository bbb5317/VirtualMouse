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

    // ── Detection ──────────────────────────────────────────────────────────

    /// <summary>
    /// Brightness threshold (0–255) applied AFTER background subtraction.
    /// Only bright moving pixels are considered markers.
    /// Raise this if faint motion (e.g. slight camera vibration) creates noise.
    /// </summary>
    public int BrightnessThreshold { get; set; } = 180;

    /// <summary>Minimum blob area in pixels (filters single-pixel noise).</summary>
    public double MinBlobArea { get; set; } = 4.0;

    /// <summary>
    /// Maximum blob area in pixels.
    /// Because background subtraction already removes static keys, this can
    /// be set generously — it mainly guards against two markers merging.
    /// </summary>
    public double MaxBlobArea { get; set; } = 600.0;

    // ── Camera Exposure (CRITICAL for background subtraction) ────────────────

    /// <summary>
    /// Manual exposure value in DirectShow log2-seconds units.
    /// -7 = 1/128s, -6 = 1/64s, -5 = 1/32s.
    /// Set to 0 to skip setting exposure (use driver default).
    /// Auto-exposure is always disabled regardless of this value.
    /// </summary>
    public double ManualExposure { get; set; } = -6;

    /// <summary>
    /// Manual gain value (0–255 typical range, driver-dependent).
    /// Set to -1 to skip setting gain.
    /// </summary>
    public double ManualGain { get; set; } = 0;

    // ── Background Subtraction (MOG2) ──────────────────────────────────────

    /// <summary>
    /// Learning rate for the MOG2 background model.
    /// 0.001–0.01 is a good range: slow enough that moving markers are not
    /// absorbed into the background, fast enough to adapt to lighting drift.
    /// </summary>
    public double BackgroundLearningRate { get; set; } = 0.005;

    // ── Grouping ───────────────────────────────────────────────────────────

    /// <summary>Maximum pixel distance between blobs to be grouped into one finger.</summary>
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
