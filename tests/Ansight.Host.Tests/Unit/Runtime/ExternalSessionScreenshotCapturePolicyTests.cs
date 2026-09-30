using SkiaSharp;
using Ansight.Adb;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ExternalSessionScreenshotCapturePolicyTests
{
    [Fact]
    public void StandardProfileAndPolicyUseTheRequestedInterval()
    {
        var request = new ExternalSessionScreenshotCaptureRequest(1024, 1_000);
        Assert.Equal(1_000, ExternalSessionScreenshotCaptureProfile.Standard(request).Interval.TotalMilliseconds);
        Assert.Equal(400, ExternalSessionScreenshotCaptureProfile.TestRun(request).Interval.TotalMilliseconds);
        var policy = ExternalSessionScreenshotCapturePolicy.Host("appium-xcuitest", request.MaxWidth,
            request.IntervalMilliseconds).ToResponsePayload();
        Assert.Equal(1_000, policy["sessionJpegCapture"]!["intervalMilliseconds"]!.GetValue<int>());
    }

    [Fact]
    public void DeviceRunScreenshotAcceptsConnectedPhysicalAndroidSerialOnly()
    {
        var devices = new[]
        {
            new AdbDevice("R58N123", "device", null, null, null, null),
            new AdbDevice("R58N124", "offline", null, null, null, null)
        };
        Assert.True(ExternalSessionScreenshotCaptureManager.HasConnectedAndroidDevice(devices, "R58N123"));
        Assert.False(ExternalSessionScreenshotCaptureManager.HasConnectedAndroidDevice(devices, "R58N124"));
        Assert.False(ExternalSessionScreenshotCaptureManager.HasConnectedAndroidDevice(devices, "R58N125"));
    }

    [Fact]
    public void HostPolicy_SerializesBackwardCompatibleControlPayload()
    {
        var payload = ExternalSessionScreenshotCapturePolicy.Host("simctl", 1_024).ToResponsePayload();
        var capture = Assert.IsType<JsonObject>(payload["sessionJpegCapture"]);

        Assert.Equal("host", capture["mode"]?.GetValue<string>());
        Assert.Equal("simctl", capture["source"]?.GetValue<string>());
        Assert.Equal("app", capture["fallbackMode"]?.GetValue<string>());
        Assert.Equal(2_000, capture["intervalMilliseconds"]?.GetValue<int>());
        Assert.Equal(60, capture["quality"]?.GetValue<int>());
        Assert.Equal(1_024, capture["maxWidth"]?.GetValue<int>());
        Assert.Equal(1, ExternalSessionScreenshotCapturePolicy.ControlVersion);
        Assert.Equal(
            "sessionJpegCaptureControlVersion",
            ExternalSessionScreenshotCapturePolicy.ControlVersionPropertyName);
    }

    [Fact]
    public void CaptureRequest_UsesAdvertisedMaximumWidth()
    {
        var request = ExternalSessionScreenshotCaptureRequest.FromPayload(
            new JsonObject
            {
                ["sessionJpegCapture"] = new JsonObject
                {
                    ["maxWidth"] = 1_024
                }
            });

        Assert.Equal(1_024, request.MaxWidth);
        Assert.Equal(1_024, request.ResolveTargetWidth(2_064));
    }

    [Fact]
    public void CaptureRequest_PreservesNativeWidthRequest()
    {
        var request = ExternalSessionScreenshotCaptureRequest.FromPayload(
            new JsonObject
            {
                ["sessionJpegCapture"] = new JsonObject
                {
                    ["maxWidth"] = null
                }
            });

        Assert.Null(request.MaxWidth);
        Assert.Equal(2_064, request.ResolveTargetWidth(2_064));
    }

    [Fact]
    public void CaptureRequest_DefaultsForOlderSdkPayloads()
    {
        var request = ExternalSessionScreenshotCaptureRequest.FromPayload(
            new JsonObject
            {
                [ExternalSessionScreenshotCapturePolicy.ControlVersionPropertyName] = 1
            });

        Assert.Equal(480, request.MaxWidth);
        Assert.Equal(480, request.ResolveTargetWidth(2_064));
    }

    [Fact]
    public void CaptureRequest_ClampsUntrustedMaximumWidth()
    {
        var request = ExternalSessionScreenshotCaptureRequest.FromPayload(
            new JsonObject
            {
                ["sessionJpegCapture"] = new JsonObject
                {
                    ["maxWidth"] = 20_000
                }
            });

        Assert.Equal(8_192, request.MaxWidth);
    }

    [Fact]
    public void TestRunProfile_CapturesTwoAndAHalfHighResolutionWebpFramesPerSecond()
    {
        var profile = ExternalSessionScreenshotCaptureProfile.TestRun(
            new ExternalSessionScreenshotCaptureRequest(480));

        Assert.Equal(400, profile.Interval.TotalMilliseconds);
        Assert.Equal(80, profile.Quality);
        Assert.Equal(1_280, profile.MaxWidth);
        Assert.Equal(1_280, profile.ResolveTargetWidth(2_064));
    }

    [Fact]
    public void FrameEncoder_ProducesQuality80WebpAtRequestedSize()
    {
        using var source = new SKBitmap(120, 240);
        source.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(source);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var profile = ExternalSessionScreenshotCaptureProfile.Standard(
            new ExternalSessionScreenshotCaptureRequest(60));

        var encoded = ExternalSessionScreenshotFrameEncoder.Encode(png.ToArray(), profile);

        Assert.Equal("webp", encoded.Format);
        Assert.Equal(60, encoded.Width);
        Assert.Equal(120, encoded.Height);
        Assert.Equal(80, encoded.Quality);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(encoded.Bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(encoded.Bytes, 8, 4));
        using var decoded = SKBitmap.Decode(encoded.Bytes);
        Assert.NotNull(decoded);
        Assert.Equal(encoded.Width, decoded.Width);
        Assert.Equal(encoded.Height, decoded.Height);
    }

    [Fact]
    public void TestRunProfile_PreservesNativeResolutionCapture()
    {
        var profile = ExternalSessionScreenshotCaptureProfile.TestRun(
            new ExternalSessionScreenshotCaptureRequest((int?)null));

        Assert.Null(profile.MaxWidth);
        Assert.Equal(2_064, profile.ResolveTargetWidth(2_064));
    }

    [Fact]
    public void AppPolicy_DoesNotDisableSdkCapture()
    {
        var payload = ExternalSessionScreenshotCapturePolicy.App("No simulator matched.")
            .ToResponsePayload();
        var capture = Assert.IsType<JsonObject>(payload["sessionJpegCapture"]);

        Assert.Equal("app", capture["mode"]?.GetValue<string>());
        Assert.Equal("No simulator matched.", capture["reason"]?.GetValue<string>());
    }

    [Fact]
    public void SupportsHostControl_RequiresVersionedSdkCapability()
    {
        Assert.False(ExternalSessionScreenshotCapturePolicy.SupportsHostControl(null));
        Assert.False(ExternalSessionScreenshotCapturePolicy.SupportsHostControl(
            new JsonObject
            {
                [ExternalSessionScreenshotCapturePolicy.ControlVersionPropertyName] = "unsupported"
            }));
        Assert.True(ExternalSessionScreenshotCapturePolicy.SupportsHostControl(
            new JsonObject
            {
                [ExternalSessionScreenshotCapturePolicy.ControlVersionPropertyName] = 1
            }));
    }
}
