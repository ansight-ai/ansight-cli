using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>A reviewable WebdriverIO/Appium test from recorded session interactions.</summary>
public sealed record AppiumScriptExtraction(
    string SuggestedName,
    string Source,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics);
