using System.Text.Json.Nodes;

namespace Ansight.Cli.Commands.App.Learning;

internal sealed record AppLearnCandidate(
    string TaskName,
    string TaskId,
    string From,
    string To,
    int StepCount,
    int Score,
    string Reason,
    JsonObject Route);
