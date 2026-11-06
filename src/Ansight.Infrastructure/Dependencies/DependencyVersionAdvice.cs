using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Dependencies;

public sealed record DependencyVersionAdvice(
    string DependencyId, string? DetectedVersion, DependencyVersionPolicy Versions,
    string Status, string? Notice);
