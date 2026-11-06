namespace Ansight.Adb;

public sealed record AdbSessionTargetResolution(AdbDevice? Device, string Message)
{
    public string Code { get; init; } = "bound";
    public IReadOnlyList<string> CandidateDeviceIds { get; init; } = [];
    public string? Recovery { get; init; }
}
