namespace VirtualMouse.Core.Models;

/// <summary>
/// Identifies which finger/hand a group of markers belongs to.
/// </summary>
public enum FingerIdentity
{
    Unknown,
    LeftThumb,    // Up to 1 marker
    LeftIndex,    // Up to 3 markers
    RightIndex,   // Up to 3 markers
    RightMiddle   // Up to 3 markers
}

/// <summary>
/// Represents a group of blobs that belong to a single finger.
/// The centroid of the group is used as the canonical position of that finger.
/// A finger is tracked regardless of how many of its markers are currently
/// visible — partial visibility (1 or 2 of 3 markers) is fully supported.
/// </summary>
public class MarkerGroup
{
    public FingerIdentity Identity { get; set; } = FingerIdentity.Unknown;
    public IReadOnlyList<MarkerBlob> Blobs { get; init; } = [];

    /// <summary>
    /// Number of markers currently visible for this finger.
    /// </summary>
    public int VisibleCount => Blobs.Count;

    /// <summary>
    /// Maximum number of markers placed on this finger.
    /// </summary>
    public int MaxCount => MaxMarkerCount(Identity);

    /// <summary>
    /// The intensity-weighted centroid position of all visible blobs in this group.
    /// This is the primary tracking coordinate for this finger.
    /// With more visible markers the centroid is more stable; with fewer it is
    /// noisier but still valid for tracking.
    /// </summary>
    public (double X, double Y) Centroid
    {
        get
        {
            if (Blobs.Count == 0) return (0, 0);
            // Intensity-weighted average: brighter blobs contribute more to the centroid
            double totalWeight = Blobs.Sum(b => b.MeanIntensity);
            if (totalWeight <= 0)
                return (Blobs.Average(b => b.X), Blobs.Average(b => b.Y));
            double wx = Blobs.Sum(b => b.X * b.MeanIntensity) / totalWeight;
            double wy = Blobs.Sum(b => b.Y * b.MeanIntensity) / totalWeight;
            return (wx, wy);
        }
    }

    /// <summary>
    /// Maximum number of markers placed on a given finger identity.
    /// </summary>
    public static int MaxMarkerCount(FingerIdentity identity) => identity switch
    {
        FingerIdentity.LeftThumb   => 1,
        FingerIdentity.LeftIndex   => 3,
        FingerIdentity.RightIndex  => 3,
        FingerIdentity.RightMiddle => 3,
        _                          => 0
    };
}
