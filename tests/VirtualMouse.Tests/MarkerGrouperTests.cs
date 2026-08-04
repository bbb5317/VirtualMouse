using Microsoft.Extensions.Logging.Abstractions;
using VirtualMouse.Core;
using VirtualMouse.Core.Models;
using Xunit;

namespace VirtualMouse.Tests;

public class MarkerGrouperTests
{
    private readonly MarkerGrouper _grouper;

    public MarkerGrouperTests()
    {
        var settings = new TrackingSettings { GroupingDistancePixels = 60.0 };
        _grouper = new MarkerGrouper(NullLogger<MarkerGrouper>.Instance, settings);
    }

    // ── Helper: build a full 10-blob layout (1+3+3+3) ─────────────────────

    private static List<MarkerBlob> FullLayout() =>
    [
        // Left Thumb (1 marker)
        new() { X = 100, Y = 400, MeanIntensity = 220 },

        // Left Index (3 markers)
        new() { X = 240, Y = 380, MeanIntensity = 220 },
        new() { X = 255, Y = 390, MeanIntensity = 220 },
        new() { X = 260, Y = 400, MeanIntensity = 220 },

        // Right Index (3 markers)
        new() { X = 590, Y = 380, MeanIntensity = 220 },
        new() { X = 605, Y = 390, MeanIntensity = 220 },
        new() { X = 610, Y = 400, MeanIntensity = 220 },

        // Right Middle (3 markers)
        new() { X = 740, Y = 380, MeanIntensity = 220 },
        new() { X = 755, Y = 390, MeanIntensity = 220 },
        new() { X = 760, Y = 400, MeanIntensity = 220 },
    ];

    // ── Full visibility ────────────────────────────────────────────────────

    [Fact]
    public void Group_WithNoBlobs_ReturnsEmptyList()
    {
        var result = _grouper.Group([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Group_FullLayout_IdentifiesAllFourFingers()
    {
        var groups = _grouper.Group(FullLayout());

        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftThumb);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightMiddle);
    }

    [Fact]
    public void Group_FullLayout_CorrectBlobCounts()
    {
        var groups = _grouper.Group(FullLayout()).ToDictionary(g => g.Identity);

        Assert.Equal(1, groups[FingerIdentity.LeftThumb].VisibleCount);
        Assert.Equal(3, groups[FingerIdentity.LeftIndex].VisibleCount);
        Assert.Equal(3, groups[FingerIdentity.RightIndex].VisibleCount);
        Assert.Equal(3, groups[FingerIdentity.RightMiddle].VisibleCount);
    }

    // ── Partial visibility: one marker per finger ──────────────────────────

    [Fact]
    public void Group_OneMarkerPerFinger_StillIdentifiesAllFourFingers()
    {
        // Only the first (leftmost) marker of each finger is visible
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 }, // Left Thumb
            new() { X = 240, Y = 380 }, // Left Index (1 of 3)
            new() { X = 590, Y = 380 }, // Right Index (1 of 3)
            new() { X = 740, Y = 380 }, // Right Middle (1 of 3)
        };

        var groups = _grouper.Group(blobs);

        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftThumb);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightMiddle);
    }

    [Fact]
    public void Group_OneMarkerPerFinger_ReportsPartialVisibility()
    {
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 },
            new() { X = 240, Y = 380 },
            new() { X = 590, Y = 380 },
            new() { X = 740, Y = 380 },
        };

        var groups = _grouper.Group(blobs).ToDictionary(g => g.Identity);

        // LeftIndex has max 3 but only 1 visible — should be tracked but partial
        Assert.Equal(1, groups[FingerIdentity.LeftIndex].VisibleCount);
        Assert.Equal(3, MarkerGroup.MaxMarkerCount(FingerIdentity.LeftIndex));
        Assert.True(groups[FingerIdentity.LeftIndex].VisibleCount < MarkerGroup.MaxMarkerCount(FingerIdentity.LeftIndex));
    }

    // ── Partial visibility: two markers per finger ─────────────────────────

    [Fact]
    public void Group_TwoMarkersPerTripleFinger_StillIdentifiesAllFourFingers()
    {
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 },                                    // Left Thumb
            new() { X = 240, Y = 380 }, new() { X = 255, Y = 390 },       // Left Index (2/3)
            new() { X = 590, Y = 380 }, new() { X = 605, Y = 390 },       // Right Index (2/3)
            new() { X = 740, Y = 380 }, new() { X = 755, Y = 390 },       // Right Middle (2/3)
        };

        var groups = _grouper.Group(blobs);

        Assert.Equal(4, groups.Count);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftThumb);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightMiddle);
    }

    // ── Partial visibility: one finger fully occluded ──────────────────────

    [Fact]
    public void Group_OnlyThreeFingers_AssignsThreeIdentities()
    {
        // Right Middle is completely hidden — only 3 clusters visible
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 },
            new() { X = 240, Y = 380 }, new() { X = 255, Y = 390 }, new() { X = 260, Y = 400 },
            new() { X = 590, Y = 380 }, new() { X = 605, Y = 390 }, new() { X = 610, Y = 400 },
        };

        var groups = _grouper.Group(blobs);

        Assert.Equal(3, groups.Count);
        // The three visible clusters are assigned the first three spatial identities
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftThumb);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightIndex);
        Assert.DoesNotContain(groups, g => g.Identity == FingerIdentity.RightMiddle);
    }

    // ── Centroid accuracy ──────────────────────────────────────────────────

    [Fact]
    public void Group_Centroid_IsAverageOfVisibleBlobs()
    {
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 },
            // Left Index: two blobs at x=240 and x=260 → centroid x should be ~250
            new() { X = 240, Y = 380, MeanIntensity = 1 },
            new() { X = 260, Y = 380, MeanIntensity = 1 },
            new() { X = 590, Y = 380 },
            new() { X = 740, Y = 380 },
        };

        var groups = _grouper.Group(blobs).ToDictionary(g => g.Identity);
        var centroid = groups[FingerIdentity.LeftIndex].Centroid;

        Assert.Equal(250.0, centroid.X, precision: 1);
    }
}
