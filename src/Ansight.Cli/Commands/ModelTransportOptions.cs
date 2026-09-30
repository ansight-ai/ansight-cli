namespace Ansight.Cli.Commands;

internal static class ModelTransportOptions
{
    public static void Validate(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.HasFlag("openai-transport"))
        {
            throw new CliUsageException(
                "--openai-transport has been renamed to --model-transport.");
        }

        _ = ResolveModelTransport(arguments);
    }

    public static SimulatorAgentOpenAiProtocol ResolveModelTransport(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.HasFlag("model-transport"))
        {
            return SimulatorAgentOpenAiProtocol.Auto;
        }

        return arguments.GetOption("model-transport")?.Trim().ToLowerInvariant() switch
        {
            "auto" => SimulatorAgentOpenAiProtocol.Auto,
            "http" => SimulatorAgentOpenAiProtocol.Http,
            "websocket" or "ws" => SimulatorAgentOpenAiProtocol.WebSocket,
            _ => throw new CliUsageException(
                "--model-transport must be auto, websocket, or http.")
        };
    }
}
