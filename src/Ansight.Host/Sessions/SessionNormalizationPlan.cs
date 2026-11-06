namespace Ansight.Host.Sessions;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal sealed record SessionNormalizationPlan(
    IReadOnlyDictionary<string, string> ImageReplacements,
    IReadOnlyDictionary<string, string> VisualTreeReplacements);
