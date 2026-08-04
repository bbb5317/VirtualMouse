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

    // ── Vision / Detection ─────────────────────────────────────────────────

    /// <summary>Brightness threshold (0-255) for binary thresholding.</summary>
    public int BrightnessThreshold { get; set; } = 200;

    /// <summary>Minimum blob area in pixels (noise filter).</summary>
    public double MinBlobArea { get; set; } = 5.0;

    /// <summary>Maximum blob area in pixels (large-reflection filter).</summary>
    public double MaxBlobArea { get; set; } = 500.0;

    /// <summary>Maximum pixel distance between blobs to be grouped into one finger.</summary>
    public double GroupingDistancePixels { get; set; } = 60.0;

    // ── Mouse Movement ─────────────────────────────────────────────────────

    /// <summary>Screen pixels moved per camera pixel of finger movement.</summary>
    public double MouseSensitivity { get; set; } = 2.5;

    /// <summary>Minimum camera-pixel movement before cursor moves (jitter filter).</summary>
    public double DeadZonePixels { get; set; } = 0.5;

    // ── Activation Gesture (Left Hand Pinch) ──────────────────────────────

    /// <summary>
    /// Distance (in camera pixels) between the left thumb and left index centroids
    /// that serves as the activation threshold.
    /// Mouse control is ACTIVE when the distance is GREATER than this value
    /// (fingers spread apart = mouse on).
    /// Mouse control is INACTIVE when the distance is LESS than this value
    /// (fingers pinched together = mouse off).
    /// Set to -1 if not yet calibrated.
    /// </summary>
    public double ActivationThresholdPixels { get; set; } = -1;

    /// <summary>
    /// Hysteresis band around the activation threshold to prevent rapid on/off toggling.
    /// The mouse activates at (threshold + hysteresis) and deactivates at (threshold - hysteresis).
    /// </summary>
    public double ActivationHysteresisPixels { get; set; } = 15.0;

    // ── Click Gestures (Right Hand Taps) ──────────────────────────────────

    /// <summary>
    /// Maximum duration in milliseconds for a tap to be recognised as a click
    /// (as opposed to a hold/drag).
    /// </summary>
    public int TapMaxDurationMs { get; set; } = 300;

    /// <summary>
    /// Minimum distance (camera pixels) the right index finger must move downward
    /// (toward the keyboard) within TapMaxDurationMs to register as a tap.
    /// </summary>
    public double TapMinMovementPixels { get; set; } = 8.0;

    /// <summary>
    /// Duration in milliseconds after which a held-down tap becomes a click-and-hold (drag).
    /// </summary>
    public int HoldThresholdMs { get; set; } = 500;
}
