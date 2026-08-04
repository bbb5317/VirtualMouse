using VirtualMouse.Core.Models;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Core;

/// <summary>
/// Clusters detected MarkerBlobs into MarkerGroups corresponding to individual fingers.
/// Uses a simple proximity-based grouping algorithm followed by count-based identification.
/// </summary>
public class MarkerGrouper
{
    private readonly ILogger<MarkerGrouper> _logger;
    private readonly TrackingSettings _settings;

    public MarkerGrouper(ILogger<MarkerGrouper> logger, TrackingSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Groups a flat list of blobs into finger groups and assigns identities.
    /// </summary>
    public IReadOnlyList<MarkerGroup> Group(IReadOnlyList<MarkerBlob> blobs)
    {
        if (blobs.Count == 0) return [];

        // Step 1: Cluster blobs by proximity using a greedy nearest-neighbour approach.
        var clusters = ClusterByProximity(blobs, _settings.GroupingDistancePixels);

        // Step 2: Assign finger identities based on blob count and spatial position.
        var groups = AssignIdentities(clusters);

        _logger.LogDebug("Grouped {BlobCount} blobs into {GroupCount} finger groups.", blobs.Count, groups.Count);
        return groups;
    }

    private static List<List<MarkerBlob>> ClusterByProximity(IReadOnlyList<MarkerBlob> blobs, double maxDistance)
    {
        var remaining = blobs.ToList();
        var clusters = new List<List<MarkerBlob>>();

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
                        Math.Sqrt(Math.Pow(b.X - candidate.X, 2) + Math.Pow(b.Y - candidate.Y, 2)) <= maxDistance);

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

    private List<MarkerGroup> AssignIdentities(List<List<MarkerBlob>> clusters)
    {
        // Sort clusters left-to-right by their centroid X coordinate.
        // In a top-down camera view, the left hand is on the left side of the frame.
        var sorted = clusters
            .Select(c => new MarkerGroup { Blobs = c })
            .OrderBy(g => g.Centroid.X)
            .ToList();

        var groups = new List<MarkerGroup>();

        // Expected layout (left to right in camera frame):
        //   Left Hand: LeftThumb (1) + LeftIndex (3)
        //   Right Hand: RightIndex (3) + RightMiddle (3)
        //
        // We identify by count first, then by relative position.
        var singleMarkerGroups = sorted.Where(g => g.Blobs.Count == 1).ToList();
        var tripleMarkerGroups = sorted.Where(g => g.Blobs.Count == 3).ToList();

        // Left Thumb: the single-marker group on the left side
        var leftThumbGroup = singleMarkerGroups.OrderBy(g => g.Centroid.X).FirstOrDefault();
        if (leftThumbGroup != null)
        {
            leftThumbGroup.Identity = FingerIdentity.LeftThumb;
            groups.Add(leftThumbGroup);
        }

        // Triple-marker groups: sort by X position
        // Leftmost triple = LeftIndex, middle = RightIndex, rightmost = RightMiddle
        var sortedTriples = tripleMarkerGroups.OrderBy(g => g.Centroid.X).ToList();
        var identities = new[] { FingerIdentity.LeftIndex, FingerIdentity.RightIndex, FingerIdentity.RightMiddle };
        for (int i = 0; i < Math.Min(sortedTriples.Count, identities.Length); i++)
        {
            sortedTriples[i].Identity = identities[i];
            groups.Add(sortedTriples[i]);
        }

        // Log any unidentified clusters
        var unidentified = sorted.Except(groups).ToList();
        if (unidentified.Count > 0)
        {
            _logger.LogWarning("{Count} blob cluster(s) could not be identified (unexpected blob count).", unidentified.Count);
        }

        return groups;
    }
}
