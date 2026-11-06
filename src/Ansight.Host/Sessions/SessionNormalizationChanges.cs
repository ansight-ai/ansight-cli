namespace Ansight.Host.Sessions;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal readonly record struct SessionNormalizationChanges(
    int RemovedScreenshotCount,
    int RemovedVisualTreeSnapshotCount)
{
    public static SessionNormalizationChanges None => new(0, 0);

    public bool HasChanges => RemovedScreenshotCount > 0 || RemovedVisualTreeSnapshotCount > 0;
}
