namespace Ansight.Host.Files;

public sealed record ArtifactReference(string SessionId, string ArtifactId)
{
    public string Reference => $"{SessionId}/{ArtifactId}";

    public static ArtifactReference Parse(string value)
    {
        var parts = value.Split('/');

        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("An artifact reference must be <session-id>/<artifact-id>.");
        }

        return new(parts[0], parts[1]);
    }
}
