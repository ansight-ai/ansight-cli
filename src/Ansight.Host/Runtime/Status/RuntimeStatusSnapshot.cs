namespace Ansight.Host.Runtime.Status;

public sealed record RuntimeStatusSnapshot(
    bool IsRunning,
    IReadOnlyList<string> StartupWarnings,
    string BaseFolderPath,
    DateTimeOffset CapturedUtc);
