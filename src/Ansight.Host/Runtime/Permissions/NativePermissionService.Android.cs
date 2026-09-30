using System.Globalization;
using System.Text.RegularExpressions;
using Ansight.Adb;

namespace Ansight.Host.Runtime.Permissions;

internal sealed partial class NativePermissionService
{
    private async Task<PermissionResult> AndroidAsync(DeviceDescriptor target, string app, string action,
        string permission, bool shared, CancellationToken token)
    {
        if (action == "reset") return Unsupported(action, permission, target, app, "Android reset is not supported; use revoke to deny access.");
        if (!shared && !Regex.IsMatch(permission, @"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$"))
            throw new ArgumentException("A fully qualified Android permission identifier is required.");
        var adb = AdbToolLocator.Resolve(options.AdbPath);
        if (!adb.IsFound) throw new IOException(adb.Message);
        async Task<string> Shell(string command) => (await commands.RunAsync(adb.AdbPath!,
            ["-s", target.Identifier, "shell", command], token).ConfigureAwait(false)).RequireText();
        var userText = await Shell("am get-current-user").ConfigureAwait(false);
        if (!int.TryParse(userText, out var user) || user < 0) throw new IOException("The active Android user could not be resolved.");
        var dump = await Shell("dumpsys package " + DeviceCommandRunner.Quote(app)).ConfigureAwait(false);
        var userSection = ReadAndroidUserSection(dump, user);
        if (userSection is null || !Regex.IsMatch(userSection, @"\binstalled=true\b"))
            return Unsupported(action, permission, target, app, "The package is not installed for the active Android user.");
        var declared = ReadRequestedAndroidPermissions(dump);
        if (declared.Count == 0) return Unsupported(action, permission, target, app, "The package is missing or declares no permissions.");
        var api = 0;
        var targetSdk = 0;
        if (shared)
        {
            if (!int.TryParse(await Shell("getprop ro.build.version.sdk").ConfigureAwait(false), out api))
                throw new IOException("Android API level could not be resolved.");
            var targetMatch = Regex.Match(dump, @"\btargetSdk=(\d+)");
            if (!targetMatch.Success || !int.TryParse(targetMatch.Groups[1].Value, out targetSdk))
                throw new IOException("The app's Android target SDK could not be resolved.");
        }
        var candidates = shared ? PermissionCatalog.Android(permission, api, targetSdk) : [permission];
        if (candidates.Length == 0) return Unsupported(action, permission, target, app, "This permission has no runtime permission equivalent on this Android version.");
        var native = candidates.Where(declared.Contains).ToList();
        if (native.Count == 0) return Unsupported(action, permission, target, app, "The app does not declare a native permission for this resource.");
        // Background location needs foreground access first. Revocation only removes background access.
        if (shared && permission == "locationAlways" && api >= 29 && action == "grant")
            native.InsertRange(0, new[] { "android.permission.ACCESS_COARSE_LOCATION", "android.permission.ACCESS_FINE_LOCATION" }.Where(declared.Contains));
        var selected = "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
        var hasSelected = shared && permission == "photos" && api >= 34 && declared.Contains(selected);
        if (hasSelected && action == "revoke") native.Add(selected);
        string? failure = null;
        if (action != "query")
        {
            foreach (var name in native)
            {
                try
                {
                    await Shell("pm " + action + " --user " + user.ToString(CultureInfo.InvariantCulture)
                        + " " + DeviceCommandRunner.Quote(app) + " " + DeviceCommandRunner.Quote(name)).ConfigureAwait(false);
                }
                catch (IOException exception) { failure = exception.Message; break; }
            }
            // Never report the requested state as though it had been observed.
            dump = await Shell("dumpsys package " + DeviceCommandRunner.Quote(app)).ConfigureAwait(false);
        }
        var states = native.Select(name => new NativePermissionState(name, ReadAndroidState(dump, user, name))).ToList();
        var status = Aggregate(states);
        if (hasSelected && !native.Contains(selected))
        {
            var selectedStatus = ReadAndroidState(dump, user, selected);
            states.Add(new(selected, selectedStatus));
            if (status == "denied" && selectedStatus == "granted") status = "limited";
        }
        return new PermissionResult(action, permission, target.Platform, target.Identifier, app, true,
            failure is null, status, "adb", failure, states);
    }

    internal static HashSet<string> ReadRequestedAndroidPermissions(string dump)
    {
        var section = Regex.Match(dump, @"(?m)^\s*requested permissions:\s*\r?\n(?<permissions>(?:[ \t]+[A-Za-z_][A-Za-z0-9_.]*[ \t]*\r?\n)+)");
        return section.Success ? section.Groups["permissions"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).ToHashSet(StringComparer.Ordinal) : [];
    }

    private static string? ReadAndroidUserSection(string dump, int user)
    {
        var section = Regex.Match(dump, @"(?m)^(?<indent>[ \t]*)User " + user.ToString(CultureInfo.InvariantCulture)
            + @":(?<header>[^\r\n]*)");
        if (!section.Success) return null;
        var remainder = dump[(section.Index + section.Length)..];
        // Stop at a sibling or parent section, including Queries and Shared users.
        var boundary = Regex.Match(remainder, @"(?m)^[ \t]{0," + section.Groups["indent"].Length + @"}\S");
        return section.Groups["header"].Value + (boundary.Success ? remainder[..boundary.Index] : remainder);
    }

    internal static string ReadAndroidState(string dump, int user, string permission)
    {
        var userSection = ReadAndroidUserSection(dump, user);
        if (userSection is null) return "unknown";
        if (Regex.IsMatch(userSection, @"\binstalled=false\b")) return "unsupported";
        var pattern = @"(?m)^\s*" + Regex.Escape(permission) + @": granted=(true|false)\b";
        var match = Regex.Match(userSection, pattern);
        // Only inspect the global install section, never an earlier user's runtime grants.
        var firstUser = Regex.Match(dump, @"(?m)^\s*User \d+:");
        if (!match.Success && firstUser.Success) match = Regex.Match(dump[..firstUser.Index], pattern);
        if (!match.Success)
        {
            // Legacy shared-UID apps store runtime grants on the shared identity.
            var shared = Regex.Match(dump, @"(?m)^\s*sharedUser=SharedUserSetting\{\S+ (?<name>[A-Za-z0-9_.]+)/\d+\}");
            if (shared.Success)
            {
                var sharedSection = Regex.Match(dump, @"(?ms)^\s*SharedUser \[" + Regex.Escape(shared.Groups["name"].Value)
                    + @"\] \([^\r\n]+\):(?<data>.*?)(?=^\s*SharedUser \[|\z)");
                var sharedUser = sharedSection.Success ? ReadAndroidUserSection(sharedSection.Groups["data"].Value, user) : null;
                if (sharedUser is not null) match = Regex.Match(sharedUser, pattern);
            }
        }
        return match.Success ? match.Groups[1].Value == "true" ? "granted" : "denied" : "unknown";
    }
}
