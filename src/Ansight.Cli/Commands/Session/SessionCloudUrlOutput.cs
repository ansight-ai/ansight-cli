using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCloudUrlOutput(
    string Schema,
    SessionUrlResult Result);
