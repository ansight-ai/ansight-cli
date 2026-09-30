namespace Ansight.SimCtl;

public sealed record SimCtlInstalledApplication(
    string BundleIdentifier,
    string DisplayName,
    string BundleName,
    string? BundlePath = null,
    string? Version = null,
    string? BuildVersion = null)
{
    public string Name => !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : !string.IsNullOrWhiteSpace(BundleName)
            ? BundleName
            : BundleIdentifier;
}
