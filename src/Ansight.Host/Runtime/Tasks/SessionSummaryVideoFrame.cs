namespace Ansight.Host.Runtime.Tasks;

internal sealed record SessionSummaryVideoFrame(SessionVideoRecording Recording,
    DateTimeOffset CapturedAtUtc, long PositionUs, string Reason, int Priority);
