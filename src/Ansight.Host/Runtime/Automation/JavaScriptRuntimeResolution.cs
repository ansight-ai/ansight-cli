namespace Ansight.Host.Runtime.Automation;

internal sealed record JavaScriptRuntimeResolution(
    string ExecutablePath,
    bool IsAvailable,
    string Message);
