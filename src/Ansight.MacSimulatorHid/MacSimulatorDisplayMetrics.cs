namespace Ansight.MacSimulatorHid;

public sealed record MacSimulatorDisplayMetrics(
    double PixelWidth,
    double PixelHeight,
    double Scale)
{
    public double LogicalWidth => PixelWidth / Scale;

    public double LogicalHeight => PixelHeight / Scale;
}
