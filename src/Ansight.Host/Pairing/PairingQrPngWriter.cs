using Ansight.Host.Qr;
using SkiaSharp;

namespace Ansight.Host.Pairing;

internal static class PairingQrPngWriter
{
    private const int GraphicPixelsPerModule = 36;

    public static void Write(string payload, string outputPath, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var parentDirectory = Path.GetDirectoryName(fullOutputPath)
                              ?? throw new InvalidOperationException("The QR output path has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var pngBytes = EncodePng(payload);
        using var output = new FileStream(
            fullOutputPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        output.Write(pngBytes);
        output.Flush(flushToDisk: true);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullOutputPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static byte[] EncodePng(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, ECCLevel.M);
        var imageSize = qrData.ModuleMatrix.Count * GraphicPixelsPerModule;
        using var surface = SKSurface.Create(new SKImageInfo(imageSize, imageSize))
                            ?? throw new InvalidOperationException("Could not allocate the enrollment QR image.");
        surface.Canvas.Render(qrData, imageSize, imageSize);
        using var image = surface.Snapshot();
        using var imageData = image.Encode(SKEncodedImageFormat.Png, quality: 100)
                              ?? throw new InvalidOperationException("Could not encode the enrollment QR image.");
        return imageData.ToArray();
    }
}
