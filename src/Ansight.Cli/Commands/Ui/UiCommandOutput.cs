using System.Text.Json.Nodes;

namespace Ansight.Cli.Commands.Ui;

internal sealed record UiCommandOutput(
    string Schema,
    string Operation,
    bool Succeeded,
    string Message,
    JsonObject Result);
