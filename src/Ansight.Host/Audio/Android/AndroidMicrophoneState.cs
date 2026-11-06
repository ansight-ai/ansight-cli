using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansight.Host.Audio.Android;

internal enum AndroidMicrophoneState
{
    NotReady, Unknown, Ready, Unhealthy
}
