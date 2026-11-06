using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Dependencies;

public sealed record DependencyVersionPolicy(string Min, string Target, string? MaxExclusive = null);
