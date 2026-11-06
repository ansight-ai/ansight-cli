namespace Ansight.Adb;

public sealed record AndroidEmulatorToolResolution(
    bool IsFound,
    string EmulatorPath,
    string Source,
    string Message)
{
    public static AndroidEmulatorToolResolution Found(string emulatorPath, string source)
        => new(true, emulatorPath, source, $"Android Emulator resolved from {source}.");

    public static AndroidEmulatorToolResolution NotFound(string message)
        => new(false, string.Empty, string.Empty, message);
}
