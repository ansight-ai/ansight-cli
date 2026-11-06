using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Cli.Commands.App;
using Ansight.Host;
using Ansight.Host.Workspaces;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Cli.LocalSimulator;

internal sealed record CompanionTaskActivity(DateTimeOffset Time, string Message, int InstructionIndex);
