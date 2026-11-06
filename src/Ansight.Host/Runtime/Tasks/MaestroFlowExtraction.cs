using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>A reviewable Maestro flow derived from one recorded session interval.</summary>
public sealed record MaestroFlowExtraction(
    string SuggestedName,
    string Source,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics);
