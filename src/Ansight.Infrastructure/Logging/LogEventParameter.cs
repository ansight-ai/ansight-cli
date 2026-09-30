namespace Ansight.Infrastructure.Logging;

public readonly record struct LogEventParameter(string Name, object? Value)
{
    public static implicit operator LogEventParameter((string name, object? value) parameter)
        => new(parameter.name, parameter.value);
}
