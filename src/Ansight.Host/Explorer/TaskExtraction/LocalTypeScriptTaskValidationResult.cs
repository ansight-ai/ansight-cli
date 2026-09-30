using System.Diagnostics;
using System.Text;
using Ansight.Host.Runtime.Automation;

namespace Ansight.Host.Explorer.TaskExtraction;

internal sealed record LocalTypeScriptTaskValidationResult(
    IReadOnlyList<string> Diagnostics,
    bool IsInfrastructureFailure)
{
    public static LocalTypeScriptTaskValidationResult Success { get; } = new([], false);

    public static LocalTypeScriptTaskValidationResult InfrastructureFailure(string diagnostic)
        => new([diagnostic], true);

    public static LocalTypeScriptTaskValidationResult SourceFailure(string diagnostic)
        => new([diagnostic], false);
}
