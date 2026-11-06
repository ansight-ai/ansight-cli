using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.UiAutomation;

/// <summary>A command on a fixed app connection. Coordinates are normalized to the app viewport.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppInteractionRequest(
    string Id,
    string Command,
    double? X = null,
    double? Y = null,
    double? EndX = null,
    double? EndY = null,
    double? Scale = null,
    string? Value = null,
    int? DurationMs = null,
    string? TaskId = null,
    JsonObject? Input = null,
    IReadOnlyList<AppInteractionRequest>? Commands = null,
    AppInteractionTarget? Target = null,
    bool? ReplaceExisting = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128)
        {
            throw new ArgumentException("id must be a non-empty string of at most 128 characters.");
        }

        if (Command is not ("snapshot" or "tap" or "swipe" or "pinch" or "type" or "back" or "tasks" or "task" or "batch" or "exit"))
        {
            throw new ArgumentException("command must be snapshot, tap, swipe, pinch, type, back, tasks, task, batch, or exit.");
        }

        if (Target is not null)
        {
            if (Command is not ("tap" or "type"))
            {
                throw new ArgumentException("target is only valid for tap and type.");
            }

            Target.Validate();

            if (X is not null || Y is not null)
            {
                throw new ArgumentException("Use either target or coordinates, not both.");
            }
        }

        if (Command is "swipe" or "pinch" || Command == "tap" && Target is null)
        {
            RequireCoordinate(X, "x");
            RequireCoordinate(Y, "y");
        }
        else if (X is not null || Y is not null)
        {
            throw new ArgumentException("x/y are only valid for tap, swipe and pinch.");
        }

        if (Command == "swipe")
        {
            RequireCoordinate(EndX, "endX");
            RequireCoordinate(EndY, "endY");
        }
        else if (EndX is not null || EndY is not null)
        {
            throw new ArgumentException("endX/endY are only valid for swipe.");
        }

        if (Command == "pinch")
        {
            if (Scale is not { } scale || !double.IsFinite(scale) || scale < 0.1 || scale > 4)
            {
                throw new ArgumentException("pinch requires scale between 0.1 and 4.");
            }
        }
        else if (Scale is not null)
        {
            throw new ArgumentException("scale is only valid for pinch.");
        }

        if (Command == "type")
        {
            if (Value is null || Value.Length > 16384)
            {
                throw new ArgumentException("type requires value (at most 16384 characters).");
            }
        }
        else if (Value is not null)
        {
            throw new ArgumentException("value is only valid for type.");
        }

        if (ReplaceExisting is not null && (Command != "type" || Target is null))
        {
            throw new ArgumentException("replaceExisting is only valid for type with a semantic target.");
        }

        if (DurationMs is not null
            && (Command is not ("swipe" or "pinch") || DurationMs < 50 || DurationMs > 2000))
        {
            throw new ArgumentException("durationMs is only valid for swipe/pinch and must be between 50 and 2000.");
        }

        if (Command == "task")
        {
            if (string.IsNullOrWhiteSpace(TaskId) || TaskId.Length > 256)
            {
                throw new ArgumentException("task requires taskId (at most 256 characters).");
            }
        }
        else if (TaskId is not null || Input is not null)
        {
            throw new ArgumentException("taskId/input are only valid for task.");
        }

        if (Command == "batch")
        {
            if (Commands is null || Commands.Count is < 1 or > 32)
            {
                throw new ArgumentException("batch requires between 1 and 32 commands.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal) { Id };

            foreach (var child in Commands)
            {
                if (child is null || child.Command is "batch" or "exit")
                {
                    throw new ArgumentException("Batch commands cannot be null, nested batches, or exit.");
                }

                child.Validate();

                if (!ids.Add(child.Id))
                {
                    throw new ArgumentException("Batch and child ids must be unique within the batch.");
                }
            }
        }
        else if (Commands is not null)
        {
            throw new ArgumentException("commands is only valid for batch.");
        }
    }

    private static void RequireCoordinate(double? value, string name)
    {
        if (value is null || !double.IsFinite(value.Value) || value < 0 || value > 1)
        {
            throw new ArgumentException($"{name} must be a finite normalized coordinate between 0 and 1.");
        }
    }
}
