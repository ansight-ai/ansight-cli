using System.Text.Json.Nodes;

namespace Ansight.Cli.Commands.Keyboard;

internal sealed record KeyboardCommandOutput(
    string Schema,
    string Operation,
    bool Succeeded,
    string Message,
    JsonObject Result);
