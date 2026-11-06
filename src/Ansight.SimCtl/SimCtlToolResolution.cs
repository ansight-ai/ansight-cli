namespace Ansight.SimCtl;

public sealed record SimCtlToolResolution(
    bool IsFound,
    string DeveloperDirectory,
    string XcrunPath,
    string SimCtlPath,
    string Source,
    string Message)
{
    public static SimCtlToolResolution Found(
        string developerDirectory,
        string xcrunPath,
        string simCtlPath,
        string source)
        => new(
            true,
            developerDirectory,
            xcrunPath,
            simCtlPath,
            source,
            $"SimCtl was found in '{developerDirectory}'.");

    public static SimCtlToolResolution NotFound(string message)
        => new(false, string.Empty, string.Empty, string.Empty, string.Empty, message);
}
