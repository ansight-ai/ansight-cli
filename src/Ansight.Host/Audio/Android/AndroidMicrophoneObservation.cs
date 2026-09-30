using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansight.Host.Audio.Android;

internal enum AndroidMicrophoneState { NotReady, Unknown, Ready, Unhealthy }

internal sealed record AndroidMicrophoneObservation(
    AndroidMicrophoneState State,
    string Code,
    string Message,
    string? StreamIdentity = null,
    double? MaximumPowerDb = null,
    int? LastReadAgeMs = null)
{
    public bool IsReady => State == AndroidMicrophoneState.Ready;
}

/// <summary>Conservative preflight for the emulator's null-input-stream failure. This is not an atomic native guarantee.</summary>
internal static partial class AndroidMicrophoneParser
{
    public static AndroidMicrophoneObservation Parse(string dump)
    {
        var nowMatch = NowPattern().Match(dump ?? string.Empty);
        if (!nowMatch.Success || !TryReadTime(nowMatch.Groups[1].Value, out var now))
        {
            return Unknown("AudioFlinger did not report a recognizable current capture state.");
        }
        var active = new List<string>();
        foreach (var candidate in ThreadPattern().Split(dump!))
        {
            var block = candidate.Split("\nHistorical Thread Log", 2, StringSplitOptions.None)[0];
            if (!block.StartsWith("Input thread ", StringComparison.Ordinal) && !block.StartsWith("Record thread ", StringComparison.Ordinal)) { continue; }
            var tracks = ActiveTracksPattern().Match(block);
            if (tracks.Success && int.TryParse(tracks.Groups[1].Value, out var count) && count > 0
                && StandbyPattern().IsMatch(block))
            {
                if (count != 1) { return Unknown("Multiple active recording clients prevent safe microphone attribution."); }
                active.Add(block);
            }
        }
        if (active.Count == 0)
        {
            return new(AndroidMicrophoneState.NotReady, "microphone-not-ready", "The emulator has no active guest microphone capture. Start recording in the app, then inject the fixture.");
        }
        if (active.Count != 1) { return Unknown("Multiple active input streams prevent safe microphone attribution."); }
        var input = active[0];
        if (!BuiltInMicrophonePattern().IsMatch(input))
        {
            return Unknown("The active input is not identified as the emulator's built-in microphone.");
        }
        var readAge = ReadAgePattern().Match(input);
        var frames = FramesPattern().Match(input);
        if (!frames.Success || !long.TryParse(frames.Groups[1].Value, out var frameCount) || frameCount <= 0
            || !readAge.Success || !int.TryParse(readAge.Groups[1].Value, out var age))
        {
            return Unknown("The microphone has no verifiable captured samples or recent read timestamp.");
        }
        if (age >= 250)
        {
            return new(AndroidMicrophoneState.NotReady, "microphone-not-ready", "The microphone's captured samples are stale.", LastReadAgeMs: age);
        }

        // Each history has its own resolution. Only the first row of a continuous
        // capture has an opening bracket; retained continuation rows may have none.
        var histories = PowerHistoryPattern().Matches(input);
        if (histories.Count == 0) { return Unknown("The active microphone has no readable signal history; its input health cannot be established."); }
        var history = histories[^1];
        var rows = history.Groups[2].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length == 0) { return Unknown("The active microphone has no readable signal history; its input health cannot be established."); }
        var latest = PowerPattern().Match(rows[^1]);
        if (!latest.Success) { return Unknown("The microphone signal history contains an unrecognized row."); }
        var samples = new List<double>();
        foreach (var value in latest.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.Equals("-inf", StringComparison.OrdinalIgnoreCase)) { samples.Add(double.NegativeInfinity); }
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sample) && double.IsFinite(sample)) { samples.Add(sample); }
            else { return Unknown("The microphone signal history contains an unrecognized sample."); }
        }
        if (samples.Count == 0) { return Unknown("The microphone signal history is empty."); }
        if (!TryReadTime(latest.Groups[1].Value, out var sampleStart)
            || !double.TryParse(history.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var resolution)
            || !double.IsFinite(resolution) || resolution <= 0 || resolution > 1000)
        {
            return Unknown("The microphone signal history has no verifiable sample timing.");
        }
        var sampleAge = (now - sampleStart.AddMilliseconds((samples.Count - 1) * resolution)).TotalMilliseconds;
        if (sampleAge is < -100 or > 1000)
        {
            return Unknown("The microphone signal history is stale; its current input health cannot be established.");
        }
        var maximum = samples.Max();
        var identity = input.Split('\n', 2)[0];
        if (maximum > -40)
        {
            return new(AndroidMicrophoneState.Unhealthy, "emulator-input-unhealthy",
                "Unexpected microphone energy before injection with host microphone disabled. No injection was attempted; repair the emulator audio state before retrying.", identity, maximum, age);
        }
        return new(AndroidMicrophoneState.Ready, "ready", "Fresh, quiet guest microphone capture was observed.", identity, maximum, age);
    }

    private static AndroidMicrophoneObservation Unknown(string message)
        => new(AndroidMicrophoneState.Unknown, "microphone-state-unknown", message);

    private static bool TryReadTime(string text, out DateTime time)
        => DateTime.TryParseExact(text, ["MM-dd HH:mm:ss.fff", "MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    [GeneratedRegex("(?m)^Now:\\s+(\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?)[ \\t\\r]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NowPattern();
    [GeneratedRegex("(?m)^(?=(?:Input|Record|Output) thread )", RegexOptions.CultureInvariant)]
    private static partial Regex ThreadPattern();
    [GeneratedRegex("\\b\\d+ Tracks? of which (\\d+) are active", RegexOptions.CultureInvariant)]
    private static partial Regex ActiveTracksPattern();
    [GeneratedRegex("(?m)^\\s*Standby:\\s*no\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StandbyPattern();
    [GeneratedRegex("Last read occurred \\(msecs\\):\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ReadAgePattern();
    [GeneratedRegex("Frames read:\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FramesPattern();
    [GeneratedRegex("(?m)^\\s*Input device:[^\\r\\n]*\\(AUDIO_DEVICE_IN_BUILTIN_MIC\\)", RegexOptions.CultureInvariant)]
    private static partial Regex BuiltInMicrophonePattern();
    [GeneratedRegex("^[ \\t]+(\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?):[ \\t]*\\[?([^\\]\\r\\n]*)\\]?[ \\t\\r]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PowerPattern();
    [GeneratedRegex("(?m)^[ \\t]*Signal power history \\(resolution: ([^\\r\\n)]*) ms\\):[ \\t]*\\r?\\n((?:[ \\t]+\\d{2}-\\d{2}[^\\r\\n]*(?:\\r?\\n|$))*)", RegexOptions.CultureInvariant)]
    private static partial Regex PowerHistoryPattern();
}
