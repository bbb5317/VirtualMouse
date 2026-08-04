namespace VirtualMouse.Core.Models;

/// <summary>
/// Represents a single detected reflective marker blob in the camera frame.
/// Coordinates are in image pixels, with sub-pixel precision via intensity-weighted centroid.
/// </summary>
public record MarkerBlob
{
    /// <summary>
    /// Sub-pixel X coordinate of the marker centroid in the camera frame.
    /// </summary>
    public double X { get; init; }

    /// <summary>
    /// Sub-pixel Y coordinate of the marker centroid in the camera frame.
    /// </summary>
    public double Y { get; init; }

    /// <summary>
    /// Area of the detected blob in pixels. Used for size filtering.
    /// </summary>
    public double Area { get; init; }

    /// <summary>
    /// Mean intensity of the blob pixels. Higher values indicate more direct retro-reflection.
    /// </summary>
    public double MeanIntensity { get; init; }

    /// <summary>
    /// Timestamp of when this blob was detected, used for velocity calculations.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public override string ToString() => $"Blob @ ({X:F3}, {Y:F3}), Area={Area:F1}, Intensity={MeanIntensity:F1}";
}
