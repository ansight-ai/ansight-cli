namespace Ansight.Host.UiAutomation;

public sealed record UiViewport(
    string Platform,
    double Width,
    double Height,
    string CoordinateUnit);
