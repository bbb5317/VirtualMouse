namespace VirtualMouse.Core;

/// <summary>
/// Configuration settings for the tracking and gesture recognition pipeline.
/// These values can be adjusted via the UI calibration panel.
/// </summary>
public class TrackingSettings
{
    /// <summary>
    /// Multiplier applied to raw pixel delta to produce screen pixel movement.
    /// Higher values = faster cursor movement.
    /// </summary>
    public double MouseSensitivity { get; set; } = 2.5;

    /// <summary>
    /// Minimum pixel movement in the camera frame required to register a mouse move.
    /// Prevents micro-jitter from the hand from causing unwanted cursor drift.
    /// </summary>
    public double DeadZonePixels { get; set; } = 0.5;

    /// <summary>
    /// Maximum pixel distance between left thumb and left index centroids to register a pinch (click).
    /// </summary>
    public double PinchThresholdPixels { get; set; } = 30.0;

    /// <summary>
    /// Brightness threshold (0-255) for the binary thresholding step.
    /// Pixels above this value are considered part of a reflective marker.
    /// </summary>
    public int BrightnessThreshold { get; set; } = 200;

    /// <summary>
    /// Minimum blob area in pixels to be considered a valid marker (filters noise).
    /// </summary>
    public double MinBlobArea { get; set; } = 5.0;

    /// <summary>
    /// Maximum blob area in pixels to be considered a valid marker (filters large reflections).
    /// </summary>
    public double MaxBlobArea { get; set; } = 500.0;

    /// <summary>
    /// Maximum pixel distance between two blobs to be considered part of the same finger group.
    /// </summary>
    public double GroupingDistancePixels { get; set; } = 60.0;

    /// <summary>
    /// Camera device index (0 = first camera, 1 = second, etc.).
    /// </summary>
    public int CameraDeviceIndex { get; set; } = 0;

    /// <summary>
    /// Target frame width to request from the camera.
    /// </summary>
    public int FrameWidth { get; set; } = 1280;

    /// <summary>
    /// Target frame height to request from the camera.
    /// </summary>
    public int FrameHeight { get; set; } = 800;

    /// <summary>
    /// Target frames per second to request from the camera.
    /// The OV9281 supports up to 120fps at 1280x800.
    /// </summary>
    public int TargetFps { get; set; } = 120;
}
