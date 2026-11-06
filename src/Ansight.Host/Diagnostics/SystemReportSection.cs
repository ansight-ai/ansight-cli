using System.Text.Json.Nodes;
using Ansight.Infrastructure.Dependencies;

namespace Ansight.Host.Diagnostics;

public sealed record SystemReportSection(string Status, JsonNode? Data = null, string? Message = null);
