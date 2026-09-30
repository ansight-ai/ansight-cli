using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Cli;

internal sealed class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 256,
        WriteIndented = true
    };

    private readonly bool json;
    private readonly bool silent;
    private readonly TextWriter standardOutput;
    private readonly TextWriter standardError;
    private readonly object standardErrorLock = new();

    public CliOutput(
        bool json,
        TextWriter? standardOutput = null,
        TextWriter? standardError = null,
        bool silent = false)
    {
        this.json = json;
        this.silent = silent;
        this.standardOutput = standardOutput ?? Console.Out;
        this.standardError = standardError ?? Console.Error;
    }

    public bool IsJson => json;

    public void WriteJsonLine(object value)
    {
        if (silent) return;
        standardOutput.WriteLine(JsonSerializer.Serialize(value, InteractionProtocol.jsonOptions));
        standardOutput.Flush();
    }

    public void Write(object value, Func<string>? renderText = null)
    {
        if (silent)
        {
            return;
        }

        if (json)
        {
            standardOutput.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
            return;
        }

        standardOutput.WriteLine(renderText?.Invoke() ?? value.ToString());
    }

    public void WriteText(string value)
    {
        if (silent)
        {
            return;
        }

        standardOutput.WriteLine(value);
    }

    public void WriteError(string code, string message, int exitCode)
    {
        if (silent)
        {
            return;
        }

        if (json)
        {
            Write(new CliError(code, message, exitCode));
        }
        else
        {
            lock (standardErrorLock)
            {
                standardError.WriteLine($"Error: {message}");
            }
        }
    }

    public void WriteProgress(string message)
    {
        if (silent)
        {
            return;
        }

        lock (standardErrorLock)
        {
            standardError.WriteLine(message);
            standardError.Flush();
        }
    }

    public void WriteProgressRaw(string value)
    {
        if (silent)
        {
            return;
        }

        lock (standardErrorLock)
        {
            standardError.Write(value);
            standardError.Flush();
        }
    }

    public void WritePrompt(string message)
    {
        if (silent)
        {
            return;
        }

        lock (standardErrorLock)
        {
            standardError.Write(message);
            standardError.Flush();
        }
    }

    public void WriteRaw(string standardOutputValue, string standardErrorValue)
    {
        if (silent)
        {
            return;
        }

        if (!string.IsNullOrEmpty(standardOutputValue))
        {
            standardOutput.Write(standardOutputValue);
        }

        if (!string.IsNullOrEmpty(standardErrorValue))
        {
            lock (standardErrorLock)
            {
                standardError.Write(standardErrorValue);
            }
        }
    }

}
