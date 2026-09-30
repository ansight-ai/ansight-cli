using System.Globalization;
using System.Runtime.InteropServices;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceProcessSample(int ProcessId, string Identity, long? CpuNanoseconds,
    long? ResidentBytes, long? FootprintBytes = null, long? ProportionalBytes = null);

internal static class DeviceProcessCounters
{
    private static readonly Lazy<MachTimebase> macTimebase = new(() =>
    {
        if (mach_timebase_info(out var timebase) != 0 || timebase.Denominator == 0)
            throw new IOException("The host CPU clock conversion is unavailable.");
        return timebase;
    });

    internal static long MacCpuNanoseconds(ulong ticks, uint numerator, uint denominator)
        => checked((long)((UInt128)ticks * numerator / denominator));

    internal static DeviceProcessSample ParseAndroid(int pid, string stat, string memory, long ticksPerSecond)
    {
        // The command name in parentheses can itself contain spaces and parentheses.
        var end = stat.LastIndexOf(')');
        var fields = end < 0 ? [] : stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long? cpu = null;
        var birth = "unknown";
        if (fields.Length > 19)
        {
            birth = fields[19];
            if (ticksPerSecond > 0)
                cpu = checked((long)((decimal)(long.Parse(fields[11], CultureInfo.InvariantCulture)
                    + long.Parse(fields[12], CultureInfo.InvariantCulture)) * 1_000_000_000 / ticksPerSecond));
        }
        return new DeviceProcessSample(pid, $"{pid}:{birth}", cpu,
            ReadKilobytes(memory, @"TOTAL RSS:\s*(\d+)"),
            ProportionalBytes: ReadKilobytes(memory, @"TOTAL PSS:\s*(\d+)"));
    }

    private static long? ReadKilobytes(string text, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success ? checked(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1024) : null;
    }

    internal static DeviceProcessSample ReadMac(int pid, string appBundlePath)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Simulator process counters require macOS.");
        var path = new byte[4096];
        var length = proc_pidpath(pid, path, (uint)path.Length);
        if (length <= 0) throw new IOException("The simulator process executable could not be verified.");
        var executable = Encoding.UTF8.GetString(path, 0, length).TrimEnd('\0');
        if (!executable.StartsWith(appBundlePath.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new IOException("The PID no longer belongs to the selected simulator app.");
        if (proc_pid_rusage(pid, 0, out var info) != 0)
            throw new IOException($"Process resource counters are unavailable (errno {Marshal.GetLastPInvokeError()}).");
        // XNU reports CPU in Mach absolute-time units (24 MHz on this Apple silicon host).
        // Converting with the host timebase also works on Intel, where it is commonly 1:1.
        var timebase = macTimebase.Value;
        return new DeviceProcessSample(pid, $"{pid}:{info.StartTime}",
            MacCpuNanoseconds(checked(info.UserTime + info.SystemTime), timebase.Numerator, timebase.Denominator),
            checked((long)info.ResidentSize), checked((long)info.PhysicalFootprint));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RUsageInfo
    {
        public ulong UuidLow, UuidHigh, UserTime, SystemTime, PackageIdleWakeups, InterruptWakeups,
            PageIns, WiredSize, ResidentSize, PhysicalFootprint, StartTime, ExitTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebase
    {
        public uint Numerator, Denominator;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int mach_timebase_info(out MachTimebase timebase);

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pid_rusage(int pid, int flavor, out RUsageInfo info);

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);
}
