namespace Ansight.Host.Runtime.Screenshots;

using SkiaSharp;

internal sealed record EncodedExternalSessionScreenshot(
    string Format,
    int Width,
    int Height,
    int Quality,
    byte[] Bytes);
