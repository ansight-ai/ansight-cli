using System.Text.Json.Serialization;

namespace Ansight.Infrastructure.Security;

internal sealed record EncryptedStorageEnvelope(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("ciphertext")] string Ciphertext);
