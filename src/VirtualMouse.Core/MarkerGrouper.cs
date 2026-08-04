using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Core;

/// <summary>
/// Clusters detected MarkerBlobs into MarkerGroups and assigns a FingerIdentity to each.
///
/// Design principle: a finger is tracked regardless of how many of its markers are
/// currently visible (1, 2, or 3). The three markers per finger exist to improve
/// centroid precision when all are visible, but partial visibility must never cause
/// a finger to be dropped from tracking.
///
/// Identification strategy:
///   1. Cluster all blobs by proximity (greedy nearest-neighbour).
///   2. Sort clusters left-to-right by centroid X in the camera frame.
///   3. Assign identities purely by spatial order:
///        Position 1 (leftmost)  → LeftThumb
///        Position 2             → LeftIndex
///        Position 3             → RightIndex
///        Position 4 (rightmost) → RightMiddle
///   4. Any cluster with 1–3 blobs is accepted. Clusters with 4+ blobs are
///      treated as noise (two fingers merged) and split or discarded.
/// </summary>
public class MarkerGrouper
{
    private readonly ILogger<MarkerGrouper> _logger;
    private readonly TrackingSettings _settings;

    // The four finger identities in left-to-right spatial order as seen from above.
    private static readonly FingerIdentity[] SpatialOrder =
    [
        FingerIdentity.LeftThumb,
        FingerIdentity.LeftIndex,
        FingerIdentity.RightIndex,
        FingerIdentity.RightMiddle
    ];

    public MarkerGrouper(ILogger<MarkerGrouper> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Groups a flat list of blobs into finger groups and assigns identities.
    /// Accepts 1–3 blobs per finger; partial visibility does not drop the finger.
    /// </summary>
    public IReadOnlyList<MarkerGroup> Group(IReadOnlyList<MarkerBlob> blobs)
    {
        if (blobs.Count == 0) return [];

        // Step 1: Cluster blobs by proximity.
        var clusters = ClusterByProximity(blobs, _settings.GroupingDistancePixels);

        // Step 2: Filter out noise clusters (0 blobs — shouldn't happen — or 4+ blobs
        // which likely represent two fingers that merged due to occlusion).
        var validClusters = clusters
            .Where(c => c.Count >= 1 && c.Count <= 3)
            .ToList();

        if (validClusters.Count == 0)
        {
            _logger.LogDebug("No valid clusters after filtering (total raw clusters: {N}).", clusters.Count);
            return [];
        }

        // Step 3: Sort clusters left-to-right by centroid X.
        var sorted = validClusters
            .Select(c => new MarkerGroup { Blobs = c })
            .OrderBy(g => g.Centroid.X)
            .ToList();

        // Step 4: Assign identities by spatial position.
        // We assign up to 4 identities. If fewer than 4 clusters are found (some
        // fingers fully occluded), only the visible ones are assigned.
        // If more than 4 clusters are found (spurious reflections), only the
        // 4 most prominent (largest total area) are used.
        var candidates = sorted.Count > SpatialOrder.Length
            ? sorted
                .OrderByDescending(g => g.Blobs.Sum(b => b.Area))
                .Take(SpatialOrder.Length)
                .OrderBy(g => g.Centroid.X)
                .ToList()
            : sorted;

        var groups = new List<MarkerGroup>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            candidates[i].Identity = SpatialOrder[i];
            groups.Add(candidates[i]);
        }

        _logger.LogDebug(
            "Grouped {BlobCount} blobs into {GroupCount} finger group(s): {Identities}",
            blobs.Count,
            groups.Count,
            string.Join(", ", groups.Select(g => $"{g.Identity}({g.Blobs.Count})")));

        return groups;
    }

    /// <summary>
    /// Greedy nearest-neighbour proximity clustering.
    /// Two blobs belong to the same cluster if any blob already in the cluster
    /// is within <paramref name="maxDistance"/> pixels of the candidate.
    /// </summary>
    private static List<List<MarkerBlob>> ClusterByProximity(
        IReadOnlyList<MarkerBlob> blobs, double maxDistance)
    {
        var remaining = blobs.ToList();
        var clusters  = new List<List<MarkerBlob>>();

        while (remaining.Count > 0)
        {
            var cluster = new List<MarkerBlob> { remaining[0] };
            remaining.RemoveAt(0);

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var candidate = remaining[i];
                    bool isNear = cluster.Any(b =>
                        Distance(b, candidate) <= maxDistance);

                    if (isNear)
                    {
                        cluster.Add(candidate);
                        remaining.RemoveAt(i);
                        changed = true;
                    }
                }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    private static double Distance(MarkerBlob a, MarkerBlob b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
