using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class SessionListingFilterCriteria
{
    public HashSet<string> Platforms { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DeviceFormFactors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> OsNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> OsVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> DeviceTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<int> DeviceClassCodes { get; } = [];

    public bool? IsVirtual { get; init; }

    public bool? IsEmulator { get; init; }
}
