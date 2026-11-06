using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class ScreenshotGeometryUnavailableException : ArgumentException
{
    public ScreenshotGeometryUnavailableException(string message) : base(message)
    {
    }
}
