namespace VirtualMouse.Core.Models;

/// <summary>
/// Identifies which finger/hand a group of markers belongs to.
/// </summary>
public enum FingerIdentity
{
    Unknown,
    LeftThumb,        // 1 marker
    LeftIndex,        // 3 markers
    RightIndex,       // 3 markers
    RightMiddle       // 3 markers
}

/// <summary>
/// Represents a group of blobs that belong to a single finger.
/// The centroid of the group is used as the canonical position of that finger.
/// </summary>
public class MarkerGroup
{
    public FingerIdentity Identity { get; set; } = FingerIdentity.Unknown;
    public IReadOnlyList<MarkerBlob> Blobs { get; init; } = [];

    /// <summary>
    /// The average (centroid) position of all blobs in this group.
    /// This is the primary tracking coordinate for this finger.
    /// </summary>
    public (double X, double Y) Centroid
    {
        get
        {
            if (Blobs.Count == 0) return (0, 0);
            return (Blobs.Average(b => b.X), Blobs.Average(b => b.Y));
        }
    }

    /// <summary>
    /// Expected number of markers for this finger identity.
    /// </summary>
    public static int ExpectedCount(FingerIdentity identity) => identity switch
    {
        FingerIdentity.LeftThumb  => 1,
        FingerIdentity.LeftIndex  => 3,
        FingerIdentity.RightIndex => 3,
        FingerIdentity.RightMiddle => 3,
        _ => 0
    };
}
