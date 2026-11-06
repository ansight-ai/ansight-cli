using System.Diagnostics;
using System.Text;

namespace Ansight.Cli.Commands.Update;

internal sealed record CliInstallerProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
