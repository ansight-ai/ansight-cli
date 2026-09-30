namespace Ansight.Host.Models.Pairing;

public static class PairingProtocolPolicy
{
    public static string DefaultSchema => PairingConfig.SchemaName;

    public static bool IsEnabled(PairingConfig? config)
    {
        return config is not null && IsEnabledSchema(config.Schema);
    }

    public static bool IsEnabledSchema(string? schema)
    {
        return string.Equals(schema, PairingConfig.SchemaName, StringComparison.Ordinal);
    }
}
