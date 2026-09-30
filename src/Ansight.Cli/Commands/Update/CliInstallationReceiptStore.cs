using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Cli.Commands.Update;

internal static class CliInstallationReceiptStore
{
    private const string ReceiptFileName = "install.json";

    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static CliInstallationState Load()
    {
        var receiptPath = ResolveReceiptPath();
        if (!File.Exists(receiptPath))
        {
            return new CliInstallationState(null, receiptPath);
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<CliInstallationReceipt>(
                File.ReadAllText(receiptPath),
                jsonOptions);
            return receipt is null
                ? new CliInstallationState(null, receiptPath)
                : new CliInstallationState(receipt, receiptPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CliInstallationState(null, receiptPath);
        }
    }

    public static void Save(string receiptPath, CliInstallationReceipt receipt)
    {
        var directory = Path.GetDirectoryName(receiptPath)
                        ?? throw new InvalidOperationException("The CLI installation receipt path has no directory.");
        Directory.CreateDirectory(directory);
        var pendingPath = $"{receiptPath}.new-{Environment.ProcessId}";
        try
        {
            File.WriteAllText(
                pendingPath,
                JsonSerializer.Serialize(receipt, jsonOptions) + Environment.NewLine);
            File.Move(pendingPath, receiptPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(pendingPath))
            {
                File.Delete(pendingPath);
            }
        }
    }

    private static string ResolveReceiptPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("ANSIGHT_INSTALL_RECEIPT");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath.Trim());
        }

        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var executableDirectory = Directory.GetParent(executablePath);
            var versionsDirectory = executableDirectory?.Parent;
            if (versionsDirectory is not null
                && string.Equals(versionsDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase)
                && versionsDirectory.Parent is not null)
            {
                var installerReceiptPath = Path.Combine(
                    versionsDirectory.Parent.FullName,
                    ReceiptFileName);
                if (File.Exists(installerReceiptPath))
                {
                    return installerReceiptPath;
                }
            }
        }

        var defaultRoot = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ansight",
                "cli-install")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "ansight",
                "cli");
        return Path.Combine(defaultRoot, ReceiptFileName);
    }
}
