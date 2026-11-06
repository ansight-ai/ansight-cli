namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsRunContext(
    string EvaluationId,
    string RunId,
    string SessionId,
    string AppId,
    string? AppVersion,
    string? BuildNumber,
    string? Platform,
    string? DeviceModel,
    string? OperatingSystemMajor,
    string? BuildConfiguration,
    DateTimeOffset EvaluatedAtUtc);
