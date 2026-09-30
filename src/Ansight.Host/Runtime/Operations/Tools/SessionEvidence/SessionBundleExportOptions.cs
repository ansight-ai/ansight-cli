namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class SessionBundleExportOptions
{
    public bool IncludeScreenshots { get; init; } = true;

    public bool IncludeArtifactFiles { get; init; } = true;

    public bool IncludeVisualTreePayloads { get; init; } = true;
}
