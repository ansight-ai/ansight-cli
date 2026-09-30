using Ansight.Host.Runtime.DeviceExecution;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DeviceAppIconCaptureTests
{
    [Fact]
    public void NativeOutput_NormalizesIconAndIgnoresHelperDiagnostics()
    {
        using var bitmap = new SKBitmap(512, 512);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var icon = DeviceAppIconCapture.Decode("runtime diagnostic\nansight-icon:" + Convert.ToBase64String(data.ToArray()) + "\n");
        Assert.NotNull(icon);
        Assert.Equal(256, icon.Width);
        Assert.Equal(256, icon.Height);
        Assert.Equal("png", icon.Format);
        Assert.Equal("image/png", icon.MimeType);
        var bytes = Convert.FromBase64String(icon.DataBase64!);
        Assert.Equal(bytes.Length, icon.ByteCount);
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(SKColors.CornflowerBlue, decoded.GetPixel(128, 128));
    }

    [Theory]
    [InlineData("icon provider unsupported")]
    [InlineData("ansight-icon:aGVsbG8=")]
    public void UnavailableOrInvalidImage_ReturnsNoIcon(string output)
        => Assert.Null(DeviceAppIconCapture.Decode(output));
}
