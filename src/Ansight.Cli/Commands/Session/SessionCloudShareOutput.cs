using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCloudShareOutput(
    string Schema,
    SessionShareResult Result);
