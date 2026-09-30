namespace Ansight.Adb;

public sealed record AdbToolResolution(
    bool IsFound,
    string AdbPath,
    string Source,
    string Message)
{
    public static AdbToolResolution Found(string adbPath, string source)
        => new(true, adbPath, source, $"ADB was found at '{adbPath}'.");

    public static AdbToolResolution NotFound(string message)
        => new(false, string.Empty, string.Empty, message);
}
