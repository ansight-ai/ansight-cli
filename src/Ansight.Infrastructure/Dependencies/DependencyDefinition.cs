using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Dependencies;

public sealed record DependencyDefinition(
    string Id, DependencyVersionPolicy Versions, IReadOnlyList<string> Platforms,
    string UsedBy, IReadOnlyDictionary<string, string> Providers, string? Basis = null);
