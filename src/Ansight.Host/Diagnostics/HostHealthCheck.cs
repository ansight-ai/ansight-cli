using System.Reflection;
using System.Runtime.InteropServices;
using Ansight.Adb;
using SkiaSharp;

namespace Ansight.Host.Diagnostics;

public sealed record HostHealthCheck(
    string Name,
    string Status,
    bool IsSuccess,
    bool IsRequired,
    string Message,
    string? Path)
{
    public string Signal => IsSuccess ? "green" : IsRequired ? "red" : "amber";
}
