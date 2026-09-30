using Ansight.Pairing.Models;

namespace Ansight.Host.Runtime.State;

internal readonly record struct DeviceProfileLoadResult(DeviceAppProfile? Profile, string? ProfileJson);
