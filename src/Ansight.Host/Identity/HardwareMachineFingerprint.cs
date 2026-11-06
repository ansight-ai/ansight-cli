using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ansight.Host.Identity;

internal sealed record HardwareMachineFingerprint(
    string Value,
    string Version,
    string Source);
