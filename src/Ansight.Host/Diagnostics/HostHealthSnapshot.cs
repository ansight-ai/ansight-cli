using System.Reflection;
using System.Runtime.InteropServices;
using Ansight.Adb;
using SkiaSharp;

namespace Ansight.Host.Diagnostics;

public sealed record HostHealthSnapshot(
    string Schema,
    string OperatingSystem,
    string Architecture,
    string DotNetVersion,
    string HostVersion,
    string DataDirectory,
    string LogDirectory,
    IReadOnlyList<HostHealthCheck> Checks,
    bool IsHealthy,
    DateTimeOffset GeneratedAtUtc)
{
    public string Signal => !IsHealthy
        ? "red"
        : Checks.All(static check => check.IsSuccess)
            ? "green"
            : "amber";
}
