using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal sealed record AppGraphBindingExecution(
    bool IsSuccess,
    string Message,
    JsonObject Request,
    JsonObject Response,
    JsonObject Evidence)
{
    public static AppGraphBindingExecution Success(
        JsonObject request,
        JsonObject response,
        JsonObject evidence,
        string message)
        => new(true, message, request, response, evidence);

    public static AppGraphBindingExecution Failure(
        JsonObject request,
        string message,
        JsonObject? response = null,
        JsonObject? evidence = null)
        => new(false, message, request, response ?? new JsonObject(), evidence ?? new JsonObject());
}
