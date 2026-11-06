namespace Ansight.SimCtl;

public sealed record SimCtlProcessStartRequest(
    string ExecutablePath,
    string DeveloperDirectory,
    IReadOnlyList<string> Arguments);
