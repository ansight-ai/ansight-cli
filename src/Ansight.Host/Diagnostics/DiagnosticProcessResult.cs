using System.Text.Json.Nodes;
using Ansight.Infrastructure.Dependencies;

namespace Ansight.Host.Diagnostics;

public sealed record DiagnosticProcessResult(string Status, string? Output, int? ExitCode);
