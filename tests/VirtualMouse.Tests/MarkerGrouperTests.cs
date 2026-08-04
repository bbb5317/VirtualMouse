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

    [Fact]
    public void Group_WithNoBlobs_ReturnsEmptyList()
    {
        var result = _grouper.Group([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Group_WithCorrectMarkerLayout_IdentifiesAllFourFingers()
    {
        // Simulate a top-down camera view:
        // Left side: LeftThumb (1 blob) + LeftIndex (3 blobs)
        // Right side: RightIndex (3 blobs) + RightMiddle (3 blobs)
        var blobs = new List<MarkerBlob>
        {
            // Left Thumb (1 marker, leftmost)
            new() { X = 100, Y = 400 },

            // Left Index (3 markers, clustered near x=250)
            new() { X = 240, Y = 380 }, new() { X = 255, Y = 390 }, new() { X = 260, Y = 400 },

            // Right Index (3 markers, clustered near x=600)
            new() { X = 590, Y = 380 }, new() { X = 605, Y = 390 }, new() { X = 610, Y = 400 },

            // Right Middle (3 markers, clustered near x=750)
            new() { X = 740, Y = 380 }, new() { X = 755, Y = 390 }, new() { X = 760, Y = 400 },
        };

        var groups = _grouper.Group(blobs);

        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftThumb);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.LeftIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightIndex);
        Assert.Contains(groups, g => g.Identity == FingerIdentity.RightMiddle);
    }

    [Fact]
    public void Group_LeftThumb_HasOnlyOneBlob()
    {
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 },
            new() { X = 240, Y = 380 }, new() { X = 255, Y = 390 }, new() { X = 260, Y = 400 },
            new() { X = 590, Y = 380 }, new() { X = 605, Y = 390 }, new() { X = 610, Y = 400 },
            new() { X = 740, Y = 380 }, new() { X = 755, Y = 390 }, new() { X = 760, Y = 400 },
        };

        var groups = _grouper.Group(blobs);
        var thumb = groups.First(g => g.Identity == FingerIdentity.LeftThumb);

        Assert.Single(thumb.Blobs);
    }

    [Fact]
    public void Group_TripleFingers_HaveThreeBlobs()
    {
        var blobs = new List<MarkerBlob>
        {
            new() { X = 100, Y = 400 },
            new() { X = 240, Y = 380 }, new() { X = 255, Y = 390 }, new() { X = 260, Y = 400 },
            new() { X = 590, Y = 380 }, new() { X = 605, Y = 390 }, new() { X = 610, Y = 400 },
            new() { X = 740, Y = 380 }, new() { X = 755, Y = 390 }, new() { X = 760, Y = 400 },
        };

        var groups = _grouper.Group(blobs);

        Assert.Equal(3, groups.First(g => g.Identity == FingerIdentity.LeftIndex).Blobs.Count);
        Assert.Equal(3, groups.First(g => g.Identity == FingerIdentity.RightIndex).Blobs.Count);
        Assert.Equal(3, groups.First(g => g.Identity == FingerIdentity.RightMiddle).Blobs.Count);
    }
}
