using System.Reflection;

namespace Ansight.Host.Workspaces.Authoring;

internal sealed record WorkspaceReadmeTemplate(
    string RelativeDirectoryPath,
    string Content);
