namespace Ansight.Cli;

internal sealed record CliRuntimeOptions(
    string DataDirectory,
    string? AdbPath,
    string? XcodePath,
    string? SecureStorageFilePath,
    string? SecureStorageKeyFilePath,
    int? DiscoveryPort,
    int? WebSocketPort,
    bool EnableRepositoryAutomations,
    IReadOnlyList<string> AutomationRepositoryPaths,
    string JavaScriptExecutablePath);
