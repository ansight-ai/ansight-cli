using System.Globalization;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record AndroidFrameWindow(bool Supported, long LastCompletedNanoseconds, int Frames,
    long? FramesPerSecond, bool Overflow);

internal static class AndroidFrameStatistics
{
    // gfxinfo reports a bounded history for each window. Deduplicate overlapping dumps and windows.
    public static AndroidFrameWindow Read(string text, long previousCompletion, double elapsedSeconds)
    {
        var completions = new HashSet<long>();
        var flagsIndex = -1;
        var completedIndex = -1;
        var supported = false;
        foreach (var raw in text.Split('\n'))
        {
            var cells = raw.Trim().Split(',');
            if (cells.Contains("FrameCompleted", StringComparer.Ordinal))
            {
                flagsIndex = Array.IndexOf(cells, "Flags");
                completedIndex = Array.IndexOf(cells, "FrameCompleted");
                supported = flagsIndex >= 0;
                continue;
            }
            if (completedIndex < 0 || flagsIndex < 0 || cells.Length <= Math.Max(completedIndex, flagsIndex)) continue;
            if (cells[flagsIndex] == "0"
                && long.TryParse(cells[completedIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var completed)
                && completed > 0 && completed != long.MaxValue)
                completions.Add(completed);
        }
        var last = completions.Count == 0 ? previousCompletion : completions.Max();
        var frames = completions.Count(value => value > previousCompletion);
        // If the previous boundary disappeared from a full history, a poll may have missed frames.
        var overflow = previousCompletion > 0 && completions.Count >= 120 && completions.Min() > previousCompletion;
        var fps = supported && previousCompletion > 0 && elapsedSeconds > 0 && !overflow
            ? (long?)Math.Round(frames / elapsedSeconds) : null;
        return new AndroidFrameWindow(supported, last, frames, fps, overflow);
    }
}
