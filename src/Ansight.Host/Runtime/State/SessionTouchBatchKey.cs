namespace Ansight.Host.Runtime.State;

internal readonly record struct SessionTouchBatchKey(
    string Space,
    string Unit,
    double? SurfaceWidth,
    double? SurfaceHeight,
    double? SurfaceScale);
