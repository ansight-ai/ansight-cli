namespace Ansight.Host.Pairing;

using Ansight.Host;

public static class PairingConfigExchange
{
    private const string InviteDocumentSchemaName = "ansight.enrollment-invite-document.v2";

    public static string Serialize(CachedPairingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!PairingProtocolPolicy.IsEnabled(config.Config))
        {
            throw new NotSupportedException("Only the current enrollment-invite schema is supported.");
        }

        return JsonSerializer.Serialize(
            new PairingConfigExportDocument
            {
                ExportedAtUtc = DateTimeOffset.UtcNow,
                Config = config.Config,
                Consumed = config.Consumed
            },
            JsonUtil.Pretty);
    }

    public static PairingConfigImportParseResult TryDeserializeImportPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PairingConfigImportParseResult.Failure("The selected file is empty.");
        }

        var schema = TryReadSchema(json);
        if (string.Equals(schema, PairingConfigExportDocument.SchemaName, StringComparison.Ordinal))
        {
            var result = TryDeserialize(json);
            return !result.IsSuccess || result.Config is null
                ? PairingConfigImportParseResult.Failure(result.Message)
                : PairingConfigImportParseResult.Success(
                    "Enrollment invite import ready.",
                    PairingConfigImportPayload.FromFullConfigExport(result.Config));
        }

        if (string.Equals(schema, PairingConfig.SchemaName, StringComparison.Ordinal)
            || string.Equals(schema, InviteDocumentSchemaName, StringComparison.Ordinal))
        {
            var invite = PublicPairingConfigJson.TryDeserialize(json);
            if (invite is null)
            {
                return PairingConfigImportParseResult.Failure(
                    "The selected enrollment invite could not be read.");
            }

            if (!ValidateInvite(invite, out var message))
            {
                return PairingConfigImportParseResult.Failure(message);
            }

            return PairingConfigImportParseResult.Success(
                "Enrollment invite import ready.",
                PairingConfigImportPayload.FromPublicAppConfig(
                    invite.AppId,
                    invite.AppName,
                    invite.ConfigId));
        }

        return PairingConfigImportParseResult.Failure(
            string.IsNullOrWhiteSpace(schema)
                ? "The selected file is not an Ansight enrollment invite."
                : $"Unsupported enrollment schema '{schema}'.");
    }

    public static PairingConfigDeserializeResult TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PairingConfigDeserializeResult.Failure("The selected file is empty.");
        }

        PairingConfigExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<PairingConfigExportDocument>(json, JsonUtil.Compact);
        }
        catch (JsonException)
        {
            return PairingConfigDeserializeResult.Failure("The selected file is not a valid enrollment export.");
        }

        if (document?.Config is null
            || !string.Equals(document.Schema, PairingConfigExportDocument.SchemaName, StringComparison.Ordinal))
        {
            return PairingConfigDeserializeResult.Failure(
                "The selected file is not a supported enrollment export.");
        }

        if (!ValidateInvite(document.Config, out var message))
        {
            return PairingConfigDeserializeResult.Failure(message);
        }

        return PairingConfigDeserializeResult.Success(
            "Enrollment invite import ready.",
            new CachedPairingConfig
            {
                Config = document.Config,
                Consumed = document.Consumed
            });
    }

    private static bool ValidateInvite(PairingConfig invite, out string message)
    {
        if (!PairingProtocolPolicy.IsEnabled(invite)
            || invite.MinProtocolVersion != 2
            || invite.AllowedTransports is not [PairingTransportNames.Ws]
            || string.IsNullOrWhiteSpace(invite.ConfigId)
            || string.IsNullOrWhiteSpace(invite.AppId)
            || string.IsNullOrWhiteSpace(invite.AppName)
            || invite.Enrollment is null
            || string.IsNullOrWhiteSpace(invite.Enrollment.Secret))
        {
            message = "The enrollment invite is incomplete or uses an unsupported protocol.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static string? TryReadSchema(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("schema", out var schema)
                ? schema.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
