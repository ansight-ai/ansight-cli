using SkiaSharp;

namespace Ansight.Host.UiAutomation;

internal sealed record AppInteractionVisualSample(int Width, int Height, SKColor[] Pixels)
{
    public static AppInteractionVisualSample FromPng(byte[] bytes)
    {
        using var source = SKBitmap.Decode(bytes)
            ?? throw new InvalidOperationException("The interaction screenshot could not be decoded.");
        const int width = 96;
        var height = Math.Clamp((int)Math.Round(source.Height * (width / (double)source.Width)), 1, 384);
        using var resized = source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
            ?? throw new InvalidOperationException("The interaction screenshot could not be sampled.");

        return new(width, height, resized.Pixels);
    }

    public bool DiffersFrom(AppInteractionVisualSample other)
    {
        if (Width != other.Width || Height != other.Height)
        {
            return true;
        }

        var different = 0;

        for (var index = 0; index < Pixels.Length; index++)
        {
            var left = Pixels[index];
            var right = other.Pixels[index];

            if (Math.Abs(left.Red - right.Red) > 12 || Math.Abs(left.Green - right.Green) > 12
                || Math.Abs(left.Blue - right.Blue) > 12 || Math.Abs(left.Alpha - right.Alpha) > 12)
            {
                different++;
            }
        }

        // Ignore isolated compression/rasterization noise and a blinking caret.

        return different > Pixels.Length * 0.002;
    }
}
