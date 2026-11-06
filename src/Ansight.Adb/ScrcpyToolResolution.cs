namespace Ansight.Adb;

public sealed record ScrcpyToolResolution(
    bool IsFound,
    string ExecutablePath,
    string ServerPath,
    string Version,
    string Source,
    string Message)
{
    public static ScrcpyToolResolution Found(
        string executablePath,
        string serverPath,
        string version,
        string source)
        => new(
            true,
            executablePath,
            serverPath,
            version,
            source,
            $"scrcpy {version} was found at '{executablePath}' with its matching server at '{serverPath}'.");

    public static ScrcpyToolResolution NotFound(string message)
        => new(false, string.Empty, string.Empty, string.Empty, string.Empty, message);
}
