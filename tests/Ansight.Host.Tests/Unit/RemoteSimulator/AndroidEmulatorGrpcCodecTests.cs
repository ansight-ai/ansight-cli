using Ansight.RemoteSimulator.Core.Simulator.Android;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class AndroidEmulatorGrpcCodecTests
{
    [Fact]
    public void Resolve_MapsEmulatorConsolePortToGrpcPort()
    {
        var endpoint = Endpoint.Resolve("emulator-5554");

        Assert.Equal(8554, endpoint.Port);
    }

    [Fact]
    public void Resolve_RejectsPhysicalAndroidSerial()
    {
        Assert.Throws<NotSupportedException>(() =>
            Endpoint.Resolve("R5CT123456A"));
    }

    [Fact]
    public void EncodeTouchEvent_WritesBothContacts()
    {
        var payload = MessageCodec.EncodeTouchEvent(
        [
            new AndroidEmulatorTouch(20, 30, 0, 1024),
            new AndroidEmulatorTouch(80, 90, 1, 1024),
        ]);

        Assert.Equal(
        [
            0x0A, 0x0B, 0x08, 0x14, 0x10, 0x1E, 0x18, 0x00, 0x20, 0x80, 0x08, 0x28, 0x08,
            0x0A, 0x0B, 0x08, 0x50, 0x10, 0x5A, 0x18, 0x01, 0x20, 0x80, 0x08, 0x28, 0x08,
        ],
            payload);
    }

    [Fact]
    public void DecodeImagePng_ReturnsImageField()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        byte[] message = [0x10, 0x10, 0x18, 0x20, 0x22, 0x04, .. png, 0x28, 0x02];

        var result = MessageCodec.DecodeImagePng(message);

        Assert.Equal(png, result);
    }
}
