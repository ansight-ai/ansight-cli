using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansight.Host.Audio.Android;

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
