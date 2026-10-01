using System.Text.Json;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.State;
using Ansight.Host.Utilities;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionTouchPackingStylusTests
{
    [Fact]
    public void PackAndUnpackPreserveStylusDetailsAcrossJsonStorage()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 2, 1, 2, 3, TimeSpan.Zero);
        var touch = new SessionTouchInputRecord
        {
            Id = "pencil-sample",
            Action = "move",
            CapturedAtUtc = capturedAt,
            PointerId = 5,
            PointerIndex = 0,
            PointerCount = 1,
            X = 20,
            Y = 30,
            CoordinateUnit = "points",
            Details = new SessionTouchSampleDetails
            {
                Tool = "stylus",
                SampleKind = "coalesced",
                Force = 1.25,
                MaximumPossibleForce = 4,
                AltitudeRadians = 0.7,
                AzimuthRadians = 1.2,
                RollRadians = 0.4,
                EstimatedProperties = 2,
                EstimationUpdateIndex = 42
            }
        };

        var packed = SessionTouchPacking.Pack([touch]);
        var stored = JsonSerializer.Serialize(packed, JsonUtil.Compact);
        var reloaded = JsonSerializer.Deserialize<List<SessionTouchPackedBatch>>(stored, JsonUtil.Compact);
        var restored = Assert.Single(SessionTouchPacking.Unpack(reloaded));

        Assert.Equal("pencil-sample", restored.Id);
        Assert.Equal("stylus", restored.Details?.Tool);
        Assert.Equal("coalesced", restored.Details?.SampleKind);
        Assert.Equal(1.25, restored.Details?.Force);
        Assert.Equal(0.7, restored.Details?.AltitudeRadians);
        Assert.Equal(42, restored.Details?.EstimationUpdateIndex);
    }

    [Fact]
    public void HoverActionAndDistanceSurvivePacking()
    {
        var touch = new SessionTouchInputRecord
        {
            Id = "pencil-hover",
            Action = "hoverMove",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            PointerId = 6,
            PointerIndex = 0,
            PointerCount = 1,
            X = 10,
            Y = 20,
            CoordinateUnit = "points",
            Details = new SessionTouchSampleDetails
            {
                Tool = "stylus",
                SampleKind = "hover",
                Distance = 0.3
            }
        };

        var restored = Assert.Single(SessionTouchPacking.Unpack(SessionTouchPacking.Pack([touch])));

        Assert.Equal("hoverMove", restored.Action);
        Assert.Equal(0.3, restored.Details?.Distance);
    }
}
