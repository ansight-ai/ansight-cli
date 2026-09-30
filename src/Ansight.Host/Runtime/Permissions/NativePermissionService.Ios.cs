using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Ansight.SimCtl;

namespace Ansight.Host.Runtime.Permissions;

internal sealed partial class NativePermissionService
{
    private async Task<PermissionResult> IosAsync(DeviceDescriptor target, string app, string action,
        string permission, bool shared, CancellationToken token)
    {
        var service = shared ? PermissionCatalog.Ios[permission] : permission;
        if (service is not ("location" or "location-always") && !PermissionCatalog.TccServices.ContainsKey(service))
            return Unsupported(action, permission, target, app, $"The iOS service '{service}' is unsupported by this native provider.");
        var simctl = simctlPathOverride;
        if (simctl is null)
        {
            var tool = await SimCtlToolLocator.ResolveAsync(options.XcodePath, token).ConfigureAwait(false);
            if (!tool.IsFound) throw new IOException(tool.Message);
            simctl = tool.SimCtlPath;
        }
        async Task<string> Simctl(params string[] arguments) => (await commands.RunAsync(simctl, arguments, token).ConfigureAwait(false)).RequireText();
        // Resolve installation before granting so typos cannot seed permissions for a nonexistent app.
        await Simctl("get_app_container", target.Identifier, app, "app").ConfigureAwait(false);
        if (action != "query")
        {
            var helpResult = await commands.RunAsync(simctl, ["help", "privacy"], token).ConfigureAwait(false);
            helpResult.RequireText();
            // simctl prints help to stderr even when the command succeeds.
            var help = helpResult.Text + "\n" + helpResult.Error;
            if (!Regex.IsMatch(help, @"(?m)^\s*" + Regex.Escape(service) + @"\s+-"))
                return Unsupported(action, permission, target, app, $"The installed simctl does not support changing '{service}'. Query is available where native state can be read.");
            await Simctl("privacy", target.Identifier, action, service, app).ConfigureAwait(false);
        }
        string status;
        string? message = null;
        try
        {
            var deviceList = JsonNode.Parse(await Simctl("list", "devices", "--json").ConfigureAwait(false))?["devices"]?.AsObject();
            var entries = deviceList?.SelectMany(pair => pair.Value?.AsArray().OfType<JsonObject>() ?? []);
            var device = entries?.SingleOrDefault(entry => string.Equals(entry["udid"]?.GetValue<string>(), target.Identifier, StringComparison.OrdinalIgnoreCase));
            var root = device?["dataPath"]?.GetValue<string>();
            if (root is null || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                throw new IOException("The simulator's native permission store could not be located.");
            Task<string> ReadState() => service is "location" or "location-always"
                ? ReadLocationAsync(root, app, service, token)
                : ReadTccAsync(root, app, service, token);
            status = await ReadState().ConfigureAwait(false);
            var expected = action switch { "grant" => "granted", "revoke" => "denied", "reset" => "notDetermined", _ => null };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            // Native daemons can return before persisting the authorization decision.
            while (expected is not null && status != expected && status != "unknown" && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                status = await ReadState().ConfigureAwait(false);
            }
            if (status == "unknown") message = "The native permission store has an unrecognized state or schema.";
            else if (expected is not null && status != expected)
                message = $"The native command completed, but the observed permission state remains '{status}' instead of '{expected}'.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Xml.XmlException)
        {
            status = "unknown";
            message = exception.Message;
        }
        return new PermissionResult(action, permission, target.Platform, target.Identifier, app, true, true,
            status, service is "location" or "location-always" ? "simctl-locationd" : "simctl-tcc", message,
            [new NativePermissionState(service, status)]);
    }

    private async Task<string> ReadTccAsync(string root, string app, string service, CancellationToken token)
    {
        var path = Path.Combine(root, "Library", "TCC", "TCC.db");
        if (!File.Exists(path)) throw new IOException("The simulator TCC database is unavailable.");
        async Task<JsonArray> Query(string sql)
        {
            var output = (await commands.RunAsync("/usr/bin/sqlite3", ["-readonly", "-json", path, sql], token).ConfigureAwait(false)).RequireText();
            return string.IsNullOrWhiteSpace(output) ? [] : JsonNode.Parse(output)!.AsArray();
        }
        var columns = await Query("PRAGMA table_info(access);").ConfigureAwait(false);
        var names = columns.OfType<JsonObject>().Select(column => column["name"]?.GetValue<string>()).ToHashSet();
        if (!names.Contains("client") || !names.Contains("service") || !names.Contains("client_type")) return "unknown";
        var column = names.Contains("auth_value") ? "auth_value" : names.Contains("allowed") ? "allowed" : null;
        if (column is null) return "unknown";
        // Both values come from validated identifiers and a fixed catalog, not arbitrary SQL.
        var rows = await Query($"SELECT {column} AS value FROM access WHERE client='{app}' AND client_type=0 AND service='{PermissionCatalog.TccServices[service]}';").ConfigureAwait(false);
        if (rows.Count == 0) return "notDetermined";
        if (rows.Count != 1) return "unknown";
        return ReadTccState(rows[0]!["value"]!.GetValue<int>(), column == "allowed");
    }

    private async Task<string> ReadLocationAsync(string root, string app, string service, CancellationToken token)
    {
        var path = Path.Combine(root, "Library", "Caches", "locationd", "clients.plist");
        if (!File.Exists(path)) throw new IOException("The simulator location authorization store is unavailable.");
        // Location records contain dates/data that cannot be converted to JSON by plutil.
        var output = (await commands.RunAsync("/usr/bin/plutil", ["-convert", "xml1", "-o", "-", path], token).ConfigureAwait(false)).RequireText();
        return ReadLocationPlistState(output, app, service);
    }

    internal static string ReadLocationPlistState(string xml, string app, string service)
    {
        var dictionary = XDocument.Parse(xml).Root?.Element("dict");
        if (dictionary is null) return "unknown";
        var clients = new JsonObject();
        foreach (var pair in ReadPlistDictionary(dictionary))
        {
            if (pair.Value.Name != "dict") continue;
            var fields = ReadPlistDictionary(pair.Value);
            var client = new JsonObject();
            if (fields.TryGetValue("BundleId", out var bundle) && bundle.Name == "string") client["BundleId"] = bundle.Value;
            if (fields.TryGetValue("Authorization", out var authorization))
            {
                if (authorization.Name == "integer" && int.TryParse(authorization.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    client["Authorization"] = value;
                else client["Authorization"] = authorization.Value;
            }
            clients[pair.Key] = client;
        }
        return ReadLocationState(clients, app, service);
    }

    private static Dictionary<string, XElement> ReadPlistDictionary(XElement dictionary)
    {
        var elements = dictionary.Elements().ToArray();
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < elements.Length; index += 2)
        {
            if (elements[index].Name != "key" || !result.TryAdd(elements[index].Value, elements[index + 1]))
                throw new IOException("Unrecognized location authorization plist schema.");
        }
        if (elements.Length % 2 != 0) throw new IOException("Incomplete location authorization plist dictionary.");
        return result;
    }

    internal static string ReadTccState(int value, bool legacy = false) => legacy
        ? value switch { 0 => "denied", 1 => "granted", _ => "unknown" }
        : value switch { 0 => "denied", 2 => "granted", 3 => "limited", _ => "unknown" };

    internal static string ReadLocationState(JsonObject clients, string app, string service)
    {
        var entries = clients.Where(pair => pair.Key == app || pair.Key == "i" + app || pair.Key == "i" + app + ":" || pair.Key == app + ":"
            || (pair.Value as JsonObject)?["BundleId"]?.GetValue<string>() == app).Select(pair => pair.Value as JsonObject).ToArray();
        if (entries.Length == 0) return "notDetermined";
        if (entries.Length != 1) return "unknown";
        // Reset leaves the registered client record but removes Authorization.
        if (entries[0]?["Authorization"] is null) return "notDetermined";
        if (entries[0]?["Authorization"] is not JsonValue authorization || !authorization.TryGetValue<int>(out var value)) return "unknown";
        // locationd's stored values are not CLAuthorizationStatus enum values.
        return value switch
        {
            0 => "notDetermined", 1 => "denied", 4 => "granted",
            2 => service == "location-always" ? "limited" : "granted", _ => "unknown"
        };
    }
}
