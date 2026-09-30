namespace Ansight.RemoteSimulator.Core.Runtime;

public sealed class CompositeRemoteRuntimeSource : IRemoteRuntimeSource
{
    private readonly IReadOnlyList<IRemoteRuntimeSource> sources;

    public CompositeRemoteRuntimeSource(IEnumerable<IRemoteRuntimeSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        this.sources = sources.ToArray();
        if (this.sources.Count == 0)
        {
            throw new ArgumentException("At least one remote runtime source is required.", nameof(sources));
        }
    }

    public RemoteRuntimeSnapshot Current
    {
        get
        {
            var snapshots = sources.Select(static source => source.Current).ToArray();
            return new RemoteRuntimeSnapshot(
                snapshots.Max(static snapshot => snapshot.CapturedAtUtc),
                snapshots
                    .SelectMany(static snapshot => snapshot.Devices)
                    .OrderByDescending(static device => device.IsBooted)
                    .ThenBy(static device => device.Platform, StringComparer.Ordinal)
                    .ThenByDescending(static device => device.LastBootedUtc)
                    .ThenBy(static device => device.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                JoinErrors(snapshots));
        }
    }

    private static string? JoinErrors(IEnumerable<RemoteRuntimeSnapshot> snapshots)
    {
        var errors = snapshots
            .Select(static snapshot => snapshot.Error)
            .Where(static error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return errors.Length == 0 ? null : string.Join(" ", errors);
    }
}
