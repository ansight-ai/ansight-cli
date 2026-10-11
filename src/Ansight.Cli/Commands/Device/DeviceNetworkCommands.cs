using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Devices.Network;

namespace Ansight.Cli.Commands.Device;

internal static class DeviceNetworkCommands
{
    public static Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken token)
    {
        if (CliCommandHelp.IsRequested(arguments)) return Task.FromResult(CliCommandHelp.Write(output, Help));
        return RunAsync(arguments, output, (action, value, cancellation) =>
        {
            var context = CliCommandContext.Current ?? throw new CliHostUnavailableException("Network conditioning requires the resident host. Run 'ansight host run'.");
            return context.Runtime.NetworkConditioning.ExecuteAsync(action, value, cancellation);
        }, token);
    }

    internal static async Task<int> RunAsync(CliArguments arguments, CliOutput output,
        Func<string, JsonObject, CancellationToken, Task<NetworkConditioningResult>> execute, CancellationToken token)
    {
        var action = arguments.RequirePositional(2, "network action").ToLowerInvariant();
        arguments.EnsurePositionalCount(3, "ansight device network <capabilities|get|set|reset|scenario> [options]");
        if (action is not ("capabilities" or "get" or "set" or "reset" or "scenario")) throw new CliUsageException("Expected capabilities, get, set, reset, or scenario.");
        var values = new List<string> { "data-dir", "session", "device-id", "app-id" };
        if (action is "set" or "get" or "reset") values.Add("lease");
        if (action is "set" or "scenario") values.Add("ports");
        if (action == "set") values.AddRange(["profile", "duration-ms"]);
        if (action == "scenario") values.Add("steps");
        arguments.EnsureOptionContract("ansight device network " + action,
            ["help", "json", "silent", "verbose", "diagnostic", "audit", "beta"], values.ToArray());
        var value = new JsonObject();
        foreach (var name in new[] { "session", "device-id", "app-id", "lease" })
        {
            var supplied = arguments.GetOption(name);
            if (supplied is not null) value[name switch { "session" => "sessionId", "device-id" => "deviceId", "app-id" => "appId", _ => "leaseId" }] = supplied;
        }
        if (action == "set")
        {
            value["profile"] = Parse(arguments.RequireOption("profile"), "--profile");
            if (arguments.GetOption("duration-ms") is not null) value["durationMs"] = arguments.GetRequiredIntOption("duration-ms", 1000, 300_000);
        }
        if (action == "scenario") value["steps"] = Parse(arguments.RequireOption("steps"), "--steps");
        if (arguments.GetOption("ports") is { } ports)
        {
            var array = new JsonArray();
            foreach (var part in ports.Split(','))
            {
                if (!int.TryParse(part, out var port) || port is < 1 or > 65535) throw new CliUsageException("--ports is a comma-separated list of TCP destination ports.");
                array.Add(port);
            }
            value["destinationPorts"] = array;
        }
        var result = await execute(action, value, token).ConfigureAwait(false);
        output.Write(result.Payload, () => result.Payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static JsonNode Parse(string value, string option)
    {
        try { return JsonNode.Parse(value) ?? throw new CliUsageException(option + " cannot be null."); }
        catch (JsonException error) { throw new CliUsageException(option + " requires valid JSON: " + error.Message); }
    }

    private const string Help = """
        Condition TCP traffic from a selected virtual device on a macOS host.

        ansight device network capabilities --session <id> [--json]
        ansight device network set --session <id> --profile '{"downloadKbps":512,"latencyMs":150}' [--duration-ms 120000] [--ports 443] [--json]
        ansight device network set --session <id> --lease <id> --profile '{"stall":true}'
        ansight device network scenario --session <id> --steps '[{"durationMs":5000,"profile":{"blocked":true}},{"durationMs":5000,"profile":{}}]'
        ansight device network get --lease <id> [--json]
        ansight device network reset --lease <id> [--json]

        Instead of --session, use --device-id <UDID|emulator-serial> --app-id <bundle-id>.
        Scenarios start asynchronously. Leases expire within five minutes; task-owned
        leases also reset when the task ends. Updates require --lease and keep the
        original expiry and port filter. Reset is idempotent and closes proxied sockets.
        Requires mitmdump 12.2.3 and macOS local-capture permission. TLS is not decrypted.
        iOS scope: selected app. Android scope: entire selected emulator.
        Control ports bypass shaping. UDP/QUIC, packet loss and radio state are not shaped.
        blocked closes TCP sockets; it does not make the OS report offline.
        """;
}
