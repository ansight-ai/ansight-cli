using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Dependencies;

public sealed record DependencyBaselineManifest(
    int SchemaVersion, string RecordedOn, string RecordedPlatform, string Basis, string Notes,
    IReadOnlyList<DependencyDefinition> Dependencies);
