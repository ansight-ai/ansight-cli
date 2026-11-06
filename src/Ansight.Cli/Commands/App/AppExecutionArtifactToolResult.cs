using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed record AppExecutionArtifactToolResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}
