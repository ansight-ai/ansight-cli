using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Runtime.Permissions;
using Ansight.MacSimulatorHid;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.SimCtl;

namespace Ansight.Host.Devices;

internal sealed class CompanionAppTestingService
{
    private static readonly string[] privacyServices =
    [
        "calendar", "contacts", "contacts-limited", "location", "location-always",
        "photos", "photos-add", "media-library", "microphone", "motion", "reminders", "siri"
    ];

    private static readonly string[] textSizes =
    [
        "extra-small", "small", "medium", "large", "extra-large", "extra-extra-large",
        "extra-extra-extra-large", "accessibility-medium", "accessibility-large",
        "accessibility-extra-large", "accessibility-extra-extra-large", "accessibility-extra-extra-extra-large"
    ];

    private readonly Func<CancellationToken, Task<DeviceInventory>> inventory;
    private readonly RuntimeOptions options;
    private readonly IDeviceCommandRunner commands;
    private readonly NativePermissionService permissions;
    private readonly string? simctlPathOverride;
    private readonly SemaphoreSlim gate = new(1, 1);

    public CompanionAppTestingService(
        Func<CancellationToken, Task<DeviceInventory>> inventory,
        RuntimeOptions options,
        IDeviceCommandRunner? commands = null,
        string? simctlPathOverride = null)
    {
        this.inventory = inventory;
        this.options = options;
        this.commands = commands ?? new DeviceCommandRunner();
        this.simctlPathOverride = simctlPathOverride;
        permissions = new NativePermissionService(inventory, this.commands, options, simctlPathOverride);
    }

    public static bool SupportsAction(string action)
    {
        return action is "app-capabilities" or "permission-grant" or "permission-deny" or "permission-reset"
            or "app-background" or "app-resume" or "app-restart" or "push" or "open-link"
            or "display-rotation" or "display-appearance" or "display-text-size";
    }

    public async Task<RemoteDeviceTestingResult> ExecuteAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = await inventory(cancellationToken).ConfigureAwait(false);
            var target = devices.Devices.SingleOrDefault(device =>
                string.Equals(device.Identifier, request.DeviceUdid, StringComparison.OrdinalIgnoreCase));
            var supportedTarget = target is not null
                && target.IsVirtual
                && target.IsBooted
                && target.IsAvailable
                && target.Platform == DevicePlatforms.Ios;
            if (!supportedTarget)
            {
                return new RemoteDeviceTestingResult(request.Action == "app-capabilities",
                    "These app and display controls require a booted iOS Simulator.",
                    new JsonObject { ["available"] = false });
            }

            var resolution = simctlPathOverride is null
                ? await SimCtlToolLocator.ResolveAsync(options.XcodePath, cancellationToken).ConfigureAwait(false)
                : SimCtlToolResolution.Found("", "", simctlPathOverride, "test");
            if (!resolution.IsFound)
            {
                return new RemoteDeviceTestingResult(false, resolution.Message);
            }

            return await ExecuteIosAsync(request, resolution, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or InvalidOperationException or UnauthorizedAccessException or JsonException
            or MacSimulatorHidException or System.ComponentModel.Win32Exception)
        {
            return new RemoteDeviceTestingResult(false, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RemoteDeviceTestingResult> ExecuteIosAsync(
        RemoteDeviceTestingRequest request,
        SimCtlToolResolution tool,
        CancellationToken token)
    {
        async Task Run(params string[] arguments)
        {
            var result = await commands.RunAsync(tool.SimCtlPath, arguments, token).ConfigureAwait(false);
            result.RequireText();
        }

        if (request.Action == "app-capabilities")
        {
            var result = await commands.RunAsync(tool.SimCtlPath, ["help", "privacy"], token).ConfigureAwait(false);
            result.RequireText();
            var help = result.Text + "\n" + result.Error;
            var supported = privacyServices.Where(service =>
                Regex.IsMatch(help, @"(?m)^\s*" + Regex.Escape(service) + @"\s+-"));
            return new RemoteDeviceTestingResult(true, "Testing controls are ready.", new JsonObject
            {
                ["available"] = true,
                ["permissions"] = new JsonArray(supported.Select(service => (JsonNode?)JsonValue.Create(service)).ToArray())
            });
        }

        if (request.Action.StartsWith("display-", StringComparison.Ordinal))
        {
            return await SetDisplayAsync(request, tool, token).ConfigureAwait(false);
        }

        if (request.Action == "open-link")
        {
            ValidateLink(request.Value);
            await Run("openurl", request.DeviceUdid, request.Value!).ConfigureAwait(false);
            return new RemoteDeviceTestingResult(true, "Link opened. The simulator chooses the app registered for this link.");
        }

        var validApp = request.AppId is not null
            && request.AppId.Length <= 255
            && Regex.IsMatch(request.AppId, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_-]+)+$");
        if (!validApp)
        {
            return new RemoteDeviceTestingResult(false, "Choose an installed app first.");
        }

        // Never seed permission records or target notifications at an uninstalled identifier.
        await Run("get_app_container", request.DeviceUdid, request.AppId!, "app").ConfigureAwait(false);
        if (request.Action.StartsWith("permission-", StringComparison.Ordinal))
        {
            if (!privacyServices.Contains(request.Permission, StringComparer.Ordinal))
            {
                return new RemoteDeviceTestingResult(false, "Choose a supported permission.");
            }

            var action = request.Action switch
            {
                "permission-grant" => "grant",
                "permission-deny" => "revoke",
                _ => "reset"
            };
            var result = await permissions.ExecuteAsync(action, request.Permission!, DevicePlatforms.Ios,
                request.DeviceUdid, request.AppId!, token).ConfigureAwait(false);
            return new RemoteDeviceTestingResult(result.IsSuccess,
                result.Message ?? $"Permission: {result.Status}.", new JsonObject { ["status"] = result.Status });
        }

        switch (request.Action)
        {
            case "app-background":
                using (var hid = new MacSimulatorHidSession(tool.DeveloperDirectory))
                {
                    hid.SendButton(request.DeviceUdid, MacSimulatorButton.Home, MacSimulatorButtonPhase.Down);
                    hid.SendButton(request.DeviceUdid, MacSimulatorButton.Home, MacSimulatorButtonPhase.Up);
                }
                return new RemoteDeviceTestingResult(true, "Home pressed. The foreground app moved to the background.");
            case "app-resume":
                await Run("launch", request.DeviceUdid, request.AppId!).ConfigureAwait(false);
                return new RemoteDeviceTestingResult(true, "App opened. A stopped app starts normally.");
            case "app-restart":
                await Run("launch", "--terminate-running-process", request.DeviceUdid, request.AppId!).ConfigureAwait(false);
                return new RemoteDeviceTestingResult(true, "App restarted. Saved app data is preserved.");
            case "push":
                ValidatePush(request.Payload, request.AppId!);
                var path = Path.Combine(Path.GetTempPath(), "ansight-push-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    await File.WriteAllTextAsync(path, request.Payload, token).ConfigureAwait(false);
                    await Run("push", request.DeviceUdid, request.AppId!, path).ConfigureAwait(false);
                }
                finally
                {
                    File.Delete(path);
                }
                return new RemoteDeviceTestingResult(true, "Push delivered. The app's notification permission and foreground handling control its display.");
            default:
                return new RemoteDeviceTestingResult(false, "Unknown app testing action.");
        }
    }

    private async Task<RemoteDeviceTestingResult> SetDisplayAsync(
        RemoteDeviceTestingRequest request,
        SimCtlToolResolution tool,
        CancellationToken token)
    {
        if (request.Action == "display-rotation")
        {
            var orientation = request.Value switch
            {
                "portrait" => 1,
                "upside-down" => 2,
                "landscape-left" => 3,
                "landscape-right" => 4,
                _ => throw new ArgumentException("Choose portrait or landscape orientation.")
            };
            token.ThrowIfCancellationRequested();
            using var hid = new MacSimulatorHidSession(tool.DeveloperDirectory);
            hid.SendOrientation(request.DeviceUdid, orientation);
            return new RemoteDeviceTestingResult(true, "Rotation sent. The app must support this orientation.");
        }

        var validAppearance = request.Action == "display-appearance" && request.Value is "light" or "dark";
        var validTextSize = request.Action == "display-text-size" && textSizes.Contains(request.Value, StringComparer.Ordinal);
        if (!validAppearance && !validTextSize)
        {
            return new RemoteDeviceTestingResult(false, "Choose a supported display setting.");
        }

        var setting = validAppearance ? "appearance" : "content_size";
        var result = await commands.RunAsync(tool.SimCtlPath,
            ["ui", request.DeviceUdid, setting, request.Value!], token).ConfigureAwait(false);
        result.RequireText();
        return new RemoteDeviceTestingResult(true, "Display updated.");
    }

    internal static void ValidateLink(string? value)
    {
        var invalidText = string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || value.Any(char.IsControl);
        if (invalidText || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Enter a complete app link or https URL.");
        }

        if (uri.Scheme is "file" or "javascript" or "data")
        {
            throw new ArgumentException("Use an app link or a web URL.");
        }
    }

    internal static void ValidatePush(string? payload, string appId)
    {
        if (string.IsNullOrWhiteSpace(payload) || Encoding.UTF8.GetByteCount(payload) > 4096)
        {
            throw new ArgumentException("Push JSON must be between 1 and 4096 bytes.");
        }

        var json = JsonNode.Parse(payload) as JsonObject;
        if (json?["aps"] is not JsonObject)
        {
            throw new ArgumentException("Push JSON needs an aps object.");
        }

        if (json["Simulator Target Bundle"] is JsonNode target && target.GetValue<string>() != appId)
        {
            throw new ArgumentException("The push target must match the selected app.");
        }
    }
}
