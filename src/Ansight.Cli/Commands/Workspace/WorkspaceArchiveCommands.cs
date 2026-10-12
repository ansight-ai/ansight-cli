using Ansight.Host.Workspaces.Archives;

namespace Ansight.Cli.Commands.Workspace;

internal static class WorkspaceArchiveCommands
{
    public static async Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(1, "workspace action").ToLowerInvariant();
        var overwrite = arguments.HasFlag("force") || arguments.HasFlag("overwrite");
        var service = new WorkspaceArchiveService();
        WorkspaceArchiveResult result;
        string? appId = null;
        if (action == "export")
        {
            var archivePath = arguments.RequireOption("output");
            string workspacePath;
            if (arguments.HasFlag("app-id"))
            {
                arguments.EnsurePositionalCount(2, "ansight workspace export --app-id <id> --output <archive.zip> [--force]");
                appId = arguments.RequireOption("app-id");
                var options = CliRuntime.ResolveOptions(arguments);
                await using var lease = await CliRuntimeLease.CreateAsync(options, start: false, cancellationToken).ConfigureAwait(false);
                var app = lease.Runtime.Apps.Get(appId)
                          ?? throw new CliUsageException($"App '{appId}' was not found. Use ansight app list to find a registered App ID.");
                workspacePath = !string.IsNullOrWhiteSpace(app.CodebasePath)
                    ? app.CodebasePath
                    : throw new CliUsageException($"App '{appId}' has no linked workspace. Use ansight app register {appId} --codebase <workspace-path> first.");
            }
            else
            {
                arguments.EnsurePositionalCount(3, "ansight workspace export <workspace-path> --output <archive.zip> [--force], or use --app-id <id> instead of the workspace path");
                workspacePath = arguments.RequirePositional(2, "workspace path");
            }
            result = service.Export(workspacePath, archivePath, overwrite, cancellationToken);
        }
        else
        {
            arguments.EnsurePositionalCount(4, "ansight workspace import <archive.zip> <workspace-path> [--force]");
            if (arguments.HasFlag("app-id"))
                throw new CliUsageException("Workspace import requires a destination path. Register the imported workspace separately with ansight app register.");
            result = service.Import(arguments.RequirePositional(2, "archive path"), arguments.RequirePositional(3, "workspace path"), overwrite, cancellationToken);
        }

        output.Write(new WorkspaceArchiveOutput("ansight.workspace-archive/v1", action, result, appId),
            () => result.Message + (result.IsSuccess ? $"{Environment.NewLine}Files: {result.FileCount}; expanded bytes: {result.ExpandedBytes}" : ""));
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }
}
