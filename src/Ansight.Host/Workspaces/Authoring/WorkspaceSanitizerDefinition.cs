using Ansight.Host.Workspaces;
using Ansight.Host.Trends;

namespace Ansight.Host.Workspaces.Authoring;

/// <summary>
/// A TypeScript sanitizer module discovered beneath a linked Ansight workspace.
/// </summary>
public sealed record WorkspaceSanitizerDefinition(
    string SanitizerId,
    string ModulePath);
