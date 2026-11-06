using System.Diagnostics;

namespace Ansight.Cli.Commands.Setup;

internal sealed record AndroidSetupProcessResult(int ExitCode, string Output);
