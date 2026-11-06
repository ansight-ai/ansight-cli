using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record PersistedScreenshotEvidence(
    SessionImageFrame Frame,
    string Hash,
    string ArtifactPath);
