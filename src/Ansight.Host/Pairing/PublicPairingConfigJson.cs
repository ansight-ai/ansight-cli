namespace Ansight.Host.Pairing;

using System.Text.Json;

public static class PublicPairingConfigJson
{
    public static string Serialize(PairingConfig config, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureEnabled(config);

        return JsonSerializer.Serialize(
            CreateJsonModel(config),
            indented ? JsonUtil.Pretty : JsonUtil.Compact);
    }

    public static PairingConfig? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var schema = JsonUtil.TryGetString(document.RootElement, "schema");
            if (string.Equals(schema, PairingConfig.SchemaName, StringComparison.Ordinal))
            {
                return ReturnWhenEnabled(JsonSerializer.Deserialize<PairingConfig>(json, JsonUtil.Compact));
            }

            if (!document.RootElement.TryGetProperty("invite", out var inviteElement)
                || inviteElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReturnWhenEnabled(inviteElement.Deserialize<PairingConfig>(JsonUtil.Compact));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static object CreateJsonModel(PairingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureEnabled(config);

        if (config.Enrollment is null)
        {
            throw new InvalidOperationException("Enrollment invite is missing its one-time enrollment secret.");
        }

        return new EnrollmentInviteJsonModel
        {
            Schema = config.Schema,
            InviteId = config.ConfigId,
            AppId = config.AppId,
            AppName = config.AppName,
            IssuedAt = config.IssuedAt,
            ExpiresAt = config.ExpiresAt,
            MinProtocolVersion = config.MinProtocolVersion,
            AllowedTransports = config.AllowedTransports,
            Host = config.Host,
            Enrollment = config.Enrollment
        };
    }

    private static PairingConfig? ReturnWhenEnabled(PairingConfig? config)
    {
        return PairingProtocolPolicy.IsEnabled(config) ? config : null;
    }

    private static void EnsureEnabled(PairingConfig config)
    {
        if (!PairingProtocolPolicy.IsEnabled(config))
        {
            throw new NotSupportedException("Only the current enrollment-invite schema is supported.");
        }
    }

    private sealed class EnrollmentInviteJsonModel
    {
        public required string Schema { get; init; }
        public required string InviteId { get; init; }
        public required string AppId { get; init; }
        public required string AppName { get; init; }
        public required DateTimeOffset IssuedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required int MinProtocolVersion { get; init; }
        public required string[] AllowedTransports { get; init; }
        public required PairingHost Host { get; init; }
        public required PairingEnrollment Enrollment { get; init; }
    }
}
