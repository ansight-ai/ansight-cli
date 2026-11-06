using System.Security.Cryptography;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.WebSocketSessions;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

internal static class SystemUiGeometryTestExtensions
{
    internal static JsonObject WithStatusBar(this JsonObject geometry, string state)
    {
        geometry["systemUi"]!["statusBar"]!["state"] = state;

        if (state != "present")
        {
            geometry["systemUi"]!["statusBar"]!["regions"] = new JsonArray();
        }

        return geometry;
    }
}
