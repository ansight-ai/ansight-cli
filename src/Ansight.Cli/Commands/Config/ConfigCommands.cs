namespace Ansight.Cli.Commands.Config;

internal static class ConfigCommands
{
    private const string ExplorerPathSetting = "explorer-path";
    private const string ExplorerPortSetting = "explorer-port";

    public static int Run(CliArguments arguments, CliOutput output, TextReader? input = null, bool interactive = false)
    {
        if (arguments.Positionals.Count == 1 || CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var settings = new LocalSettingsStore(
            CliRuntime.ResolveDataDirectory(arguments.GetOption("data-dir")));
        var command = arguments.RequirePositional(1, "config command").ToLowerInvariant();
        return command switch
        {
            "credentials" => CredentialSetupCommand.Run(arguments, output, input, interactive),
            "list" or "show" or "status" => WriteSettings(arguments, output, settings, command),
            "get" => GetSetting(arguments, output, settings),
            "set" => SetSetting(arguments, output, settings),
            "unset" or "remove" or "clear" => UnsetSetting(arguments, output, settings),
            _ => throw new CliUsageException(
                $"Unknown config command '{command}'. Expected list, get, set, or unset.")
        };
    }

    private static int GetSetting(
        CliArguments arguments,
        CliOutput output,
        LocalSettingsStore settings)
    {
        arguments.EnsurePositionalCount(3, "ansight config get <setting>");
        var settingName = RequireSetting(arguments, 2);
        return WriteSettings(arguments, output, settings, "get", settingName);
    }

    private static int SetSetting(
        CliArguments arguments,
        CliOutput output,
        LocalSettingsStore settings)
    {
        arguments.EnsurePositionalCount(4, "ansight config set <setting> <value>");
        var settingName = RequireSetting(arguments, 2);
        var value = arguments.RequirePositional(3, "setting value");
        if (settingName == "android-sdk-root")
            throw new CliUsageException("Use 'ansight setup android --sdk-root <path>' to validate and save an Android SDK.");
        if (settingName is "credential-provider" or "secret-key-file" or "secret-store-file" or "android-sdk-root")
            throw new CliUsageException("Use 'ansight config credentials' to validate and save credential references together.");
        if (settingName == ExplorerPathSetting)
        {
            settings.SetExplorerPath(value);
        }
        else
        {
            settings.SetExplorerPort(value);
        }

        return WriteSettings(arguments, output, settings, "set", settingName);
    }

    private static int UnsetSetting(
        CliArguments arguments,
        CliOutput output,
        LocalSettingsStore settings)
    {
        arguments.EnsurePositionalCount(3, "ansight config unset <setting>");
        var settingName = RequireSetting(arguments, 2);
        if (settingName is "credential-provider" or "secret-key-file" or "secret-store-file" or "android-sdk-root")
            throw new CliUsageException("Credential references cannot be cleared independently. Preserve the existing key and migrate credentials explicitly.");
        if (settingName == ExplorerPathSetting)
        {
            settings.ClearExplorerPath();
        }
        else
        {
            settings.ClearExplorerPort();
        }

        return WriteSettings(arguments, output, settings, "unset", settingName);
    }

    private static int WriteSettings(
        CliArguments arguments,
        CliOutput output,
        LocalSettingsStore settings,
        string action,
        string? settingName = null)
    {
        if (action is "list" or "show" or "status")
        {
            arguments.EnsurePositionalCount(2, "ansight config list");
        }

        var credentials = settings.Credentials;
        var androidSdkRoot = LocalSettingsStore.ReadAndroidSdkRoot(Path.GetDirectoryName(settings.SettingsPath)!);
        var configuredSettings = new[]
        {
            new CliConfigSetting("android-sdk-root", androidSdkRoot, androidSdkRoot is not null, "saved Android SDK"),
            new CliConfigSetting("credential-provider", credentials?.Provider, credentials is not null, "saved credential default"),
            new CliConfigSetting("secret-key-file", credentials?.KeyFile, credentials?.KeyFile is not null, "saved external key reference"),
            new CliConfigSetting("secret-store-file", credentials?.StoreFile, credentials?.StoreFile is not null, "saved store reference"),
            new CliConfigSetting(
                ExplorerPathSetting,
                settings.ExplorerPath,
                settings.ExplorerPath is not null,
                settings.ExplorerPath is null
                    ? "random capability path"
                    : settings.HasExplorerPathPreference
                        ? "saved local default"
                        : "built-in default"),
            new CliConfigSetting(
                ExplorerPortSetting,
                settings.ExplorerPort,
                settings.ExplorerPort is not null,
                settings.ExplorerPort is null
                    ? "automatic available port"
                    : settings.HasExplorerPortPreference
                        ? "saved local default"
                        : "built-in default")
        };
        var selectedSetting = configuredSettings.First(
            setting => setting.Name == (settingName ?? ExplorerPathSetting));
        var result = new CliConfigOutput(
            "ansight.cli-config/v1",
            action,
            settings.SettingsPath,
            selectedSetting,
            configuredSettings);
        output.Write(
            result,
            () => action switch
            {
                "set" => $"Saved {selectedSetting.Name}: {FormatValue(selectedSetting)}\n"
                         + "Restart the resident host for the new local explorer address to take effect.",
                "unset" => BuildUnsetMessage(selectedSetting),
                "get" => $"{FormatSetting(selectedSetting)}\nSettings: {settings.SettingsPath}",
                _ => $"{string.Join('\n', configuredSettings.Select(FormatSetting))}\n"
                     + $"Settings: {settings.SettingsPath}"
            });
        return CliExitCodes.Success;
    }

    private static string RequireSetting(CliArguments arguments, int index)
    {
        var setting = arguments.RequirePositional(index, "setting name").ToLowerInvariant();
        return setting switch
        {
            "credential-provider" or "secret-key-file" or "secret-store-file" or "android-sdk-root" => setting,
            ExplorerPathSetting or "local-explorer-path" => ExplorerPathSetting,
            ExplorerPortSetting or "local-explorer-port" => ExplorerPortSetting,
            _ => throw new CliUsageException(
                $"Unknown local setting '{setting}'. Expected '{ExplorerPathSetting}' or '{ExplorerPortSetting}'.")
        };
    }

    private static string BuildUnsetMessage(CliConfigSetting setting)
        => setting.Name == ExplorerPathSetting
            ? "Cleared explorer-path; new explorers will use a random capability path.\n"
              + "Restart the resident host for the change to take effect."
            : "Cleared explorer-port; new explorers will choose an available port.\n"
              + "Restart the resident host for the change to take effect.";

    private static string FormatSetting(CliConfigSetting setting)
        => $"{setting.Name}: {FormatValue(setting)} ({setting.Status})";

    private static string FormatValue(CliConfigSetting setting)
        => setting.Value?.ToString()
           ?? (setting.Name == ExplorerPathSetting ? "random"
               : setting.Name == ExplorerPortSetting ? "automatic" : "not configured");

    private static string BuildHelp()
        => """
           Inspect or change local Ansight CLI defaults

           Usage:
             ansight config credentials [--key-file <path>] [--non-interactive]
             ansight config list
             ansight config get explorer-path
             ansight config get explorer-port
             ansight config set explorer-path <path>
             ansight config set explorer-port <port>
             ansight config unset explorer-path
             ansight config unset explorer-port

           Credential setup validates Secret Service or an existing externally protected key.
           It saves only the provider and absolute key/store paths for terminal and systemd use.
           Existing configuration is preserved; this command does not rotate or generate keys.
           Inspect saved references with config get credential-provider|secret-key-file|secret-store-file.
           Explicit options and environment credentials override saved defaults.
           Key files must contain a base64-encoded 32-byte key and have owner-only permissions.

           Settings:
             explorer-path    Default local explorer URL path for `host run` and `serve`.
                              Use / for the loopback root or one URL-safe segment such as ansight.
             explorer-port    Default local explorer HTTP port for `host run` and `serve`.
                              Use an integer from 1 through 65535.

           Explicit `host run --serve-path`/`--serve-port` and `serve --path`/`--port`
           values override the configured defaults. New installations use `/ansight/` on port
           47231. Unset either setting to use a random capability path or available port instead.
           A fixed path is easier to discover, so use it only on a machine you trust.

           Examples:
             ansight config set explorer-path ansight
             ansight config set explorer-port 47231
             ansight config get explorer-path --json
             ansight config get explorer-port --json
             ansight config unset explorer-path
             ansight config unset explorer-port
           """;
}
