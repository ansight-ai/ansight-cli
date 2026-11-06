using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Dependencies;

public sealed record DependencyBaselineSnapshot(
    string Status, string? Sha256, DependencyBaselineManifest? Manifest);
