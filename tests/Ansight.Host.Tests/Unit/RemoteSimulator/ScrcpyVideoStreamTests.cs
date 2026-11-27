using System.Buffers.Binary;
using Ansight.RemoteSimulator.Core.Simulator.Android;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class ScrcpyVideoStreamTests
{
    [Fact]
    public async Task ReadPacketAsync_ParsesTimestampKeyFrameAndPayload()
    {
        const ulong keyFrameFlag = 1UL << 62;
        var stream = CreatePacketStream(keyFrameFlag | 123_456UL, [1, 2, 3, 4]);

        var packet = await ScrcpyVideoStream.ReadPacketAsync(stream, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.Equal(123_456, packet.PresentationTimestampMicroseconds);
        Assert.True(packet.IsKeyFrame);
        Assert.False(packet.IsConfiguration);
        Assert.Equal([1, 2, 3, 4], packet.Data);
    }

    [Fact]
    public async Task ReadPacketAsync_ParsesConfigurationPacket()
    {
        const ulong configurationFlag = 1UL << 63;
        var stream = CreatePacketStream(configurationFlag, [0, 0, 0, 1, 103]);

        var packet = await ScrcpyVideoStream.ReadPacketAsync(stream, CancellationToken.None);

        Assert.NotNull(packet);
        Assert.True(packet.IsConfiguration);
        Assert.False(packet.IsKeyFrame);
        Assert.Equal(0, packet.PresentationTimestampMicroseconds);
    }

    [Fact]
    public async Task ReadPacketAsync_ReturnsNullAtCleanEndOfStream()
    {
        using var stream = new MemoryStream();

        var packet = await ScrcpyVideoStream.ReadPacketAsync(stream, CancellationToken.None);

        Assert.Null(packet);
    }

    private static MemoryStream CreatePacketStream(ulong timestampAndFlags, byte[] payload)
    {
        var data = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), timestampAndFlags);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), checked((uint)payload.Length));
        payload.CopyTo(data, 12);

        return new MemoryStream(data);
    }

    [Fact]
    public async Task ReadVideoAsync_RepeatsCurrentCodecConfigurationForEveryKeyFrame()
    {
        const ulong configurationFlag = 1UL << 63;
        const ulong keyFrameFlag = 1UL << 62;
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x68323634); // h264
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 464);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 1024);

        using var stream = new MemoryStream();

        foreach (var packet in new[]
        {
            CreatePacketStream(configurationFlag, [103, 104]),
            CreatePacketStream(keyFrameFlag | 100, [101]),
            CreatePacketStream(200, [1]),
            CreatePacketStream(keyFrameFlag | 300, [102]),
            CreatePacketStream(configurationFlag, [110, 111]),
            CreatePacketStream(keyFrameFlag | 400, [112]),
        })
        {
            using (packet)
            {
                packet.CopyTo(stream);
            }
        }

        stream.Position = 0;

        var received = new List<ScrcpyVideoPacket>();

        await ScrcpyVideoStream.ReadVideoAsync(stream, header, (packet, _) =>
        {
            received.Add(packet);

            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(4, received.Count);
        Assert.Equal([103, 104, 101], received[0].AccessUnit);
        Assert.Equal([1], received[1].AccessUnit);
        Assert.Equal([103, 104, 102], received[2].AccessUnit);
        Assert.Equal([110, 111, 112], received[3].AccessUnit);
        Assert.Equal(300, received[2].PresentationTimestampMicroseconds);
        Assert.Equal(464, received[2].VideoWidth);
        Assert.Equal(1024, received[2].VideoHeight);
    }

    [Fact]
    public async Task ReadVideoAsync_ParsesScrcpy5SessionAndShiftedPacketFlags()
    {
        using var wire = new MemoryStream();
        wire.Write(new byte[] { 0x68, 0x32, 0x36, 0x34 });
        WriteSession(wire, 460, 1024);
        using (var config = CreatePacketStream(1UL << 62, [103, 104]))
        {
            config.CopyTo(wire);
        }

        using (var key = CreatePacketStream((1UL << 61) | 123, [101]))
        {
            key.CopyTo(wire);
        }

        using (var delta = CreatePacketStream(456, [1]))
        {
            delta.CopyTo(wire);
        }

        WriteSession(wire, 1024, 460);
        using (var key = CreatePacketStream((1UL << 61) | 789, [102]))
        {
            key.CopyTo(wire);
        }

        using (var config = CreatePacketStream(1UL << 62, [110, 111]))
        {
            config.CopyTo(wire);
        }

        using (var key = CreatePacketStream((1UL << 61) | 999, [112]))
        {
            key.CopyTo(wire);
        }

        wire.Position = 0;
        var header = new byte[12];
        await wire.ReadExactlyAsync(header);
        var received = new List<ScrcpyVideoPacket>();

        await ScrcpyVideoStream.ReadVideoAsync(wire, header, (packet, _) =>
        {
            received.Add(packet);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(4, received.Count);
        Assert.Equal([103, 104, 101], received[0].AccessUnit);
        Assert.True(received[0].IsKeyFrame);
        Assert.Equal(123, received[0].PresentationTimestampMicroseconds);
        Assert.Equal(460, received[0].VideoWidth);
        Assert.Equal(1024, received[0].VideoHeight);
        Assert.False(received[1].IsKeyFrame);
        Assert.Equal([1], received[1].AccessUnit);
        Assert.Equal(456, received[1].PresentationTimestampMicroseconds);
        Assert.Equal([102], received[2].AccessUnit); // Old codec configuration was cleared.
        Assert.Equal(1024, received[2].VideoWidth);
        Assert.Equal(460, received[2].VideoHeight);
        Assert.Equal([110, 111, 112], received[3].AccessUnit);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(460, 16385)]
    public async Task ReadPacketAsync_RejectsInvalidScrcpy5SessionDimensions(uint width, uint height)
    {
        using var stream = new MemoryStream();
        WriteSession(stream, width, height);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ScrcpyVideoStream.ReadPacketAsync(stream, CancellationToken.None, sessionMetadata: true));
    }

    private static void WriteSession(Stream stream, uint width, uint height)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x80000000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), height);
        stream.Write(header);
    }
}
