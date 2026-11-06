using System.Text.Json;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphWorkspaceApp(string AppId, string Name, string CodebasePath);
