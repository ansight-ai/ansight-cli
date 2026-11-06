using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed record AppExecutionArtifactIdentifierResolution(
    bool IsSuccess,
    string? ApplicationIdentifier,
    string Message)
{
    public static AppExecutionArtifactIdentifierResolution Success(string applicationIdentifier)
        => new(
            true,
            applicationIdentifier,
            $"Identified app ID '{applicationIdentifier}'.");

    public static AppExecutionArtifactIdentifierResolution Failure(string message)
        => new(false, ApplicationIdentifier: null, message);
}
