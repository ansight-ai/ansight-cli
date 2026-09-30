namespace Ansight.RemoteSimulator.Core.Simulator.Apple;

public static class SimulatorTextInputMapper
{
    public static IReadOnlyList<SimulatorTextKeyStroke> Map(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var strokes = new List<SimulatorTextKeyStroke>(text.Length);
        foreach (var character in text)
        {
            if (!TryMap(character, out var stroke))
            {
                throw new ArgumentException(
                    $"Character U+{(int)character:X4} cannot be represented by the Simulator HID keyboard bridge.",
                    nameof(text));
            }

            strokes.Add(stroke);
        }

        return strokes;
    }

    private static bool TryMap(char character, out SimulatorTextKeyStroke stroke)
    {
        if (character is >= 'a' and <= 'z')
        {
            stroke = new SimulatorTextKeyStroke((uint)(4 + character - 'a'), false);
            return true;
        }

        if (character is >= 'A' and <= 'Z')
        {
            stroke = new SimulatorTextKeyStroke((uint)(4 + character - 'A'), true);
            return true;
        }

        if (character is >= '1' and <= '9')
        {
            stroke = new SimulatorTextKeyStroke((uint)(30 + character - '1'), false);
            return true;
        }

        if (character == '0')
        {
            stroke = new SimulatorTextKeyStroke(39, false);
            return true;
        }

        if (TryMapSymbol(character, out stroke))
        {
            return true;
        }

        stroke = default;
        return false;
    }

    private static bool TryMapSymbol(char character, out SimulatorTextKeyStroke stroke)
    {
        stroke = character switch
        {
            '\n' or '\r' => new SimulatorTextKeyStroke(40, false),
            '\t' => new SimulatorTextKeyStroke(43, false),
            ' ' => new SimulatorTextKeyStroke(44, false),
            '-' => new SimulatorTextKeyStroke(45, false),
            '_' => new SimulatorTextKeyStroke(45, true),
            '=' => new SimulatorTextKeyStroke(46, false),
            '+' => new SimulatorTextKeyStroke(46, true),
            '[' => new SimulatorTextKeyStroke(47, false),
            '{' => new SimulatorTextKeyStroke(47, true),
            ']' => new SimulatorTextKeyStroke(48, false),
            '}' => new SimulatorTextKeyStroke(48, true),
            '\\' => new SimulatorTextKeyStroke(49, false),
            '|' => new SimulatorTextKeyStroke(49, true),
            ';' => new SimulatorTextKeyStroke(51, false),
            ':' => new SimulatorTextKeyStroke(51, true),
            '\'' => new SimulatorTextKeyStroke(52, false),
            '"' => new SimulatorTextKeyStroke(52, true),
            '`' => new SimulatorTextKeyStroke(53, false),
            '~' => new SimulatorTextKeyStroke(53, true),
            ',' => new SimulatorTextKeyStroke(54, false),
            '<' => new SimulatorTextKeyStroke(54, true),
            '.' => new SimulatorTextKeyStroke(55, false),
            '>' => new SimulatorTextKeyStroke(55, true),
            '/' => new SimulatorTextKeyStroke(56, false),
            '?' => new SimulatorTextKeyStroke(56, true),
            '!' => new SimulatorTextKeyStroke(30, true),
            '@' => new SimulatorTextKeyStroke(31, true),
            '#' => new SimulatorTextKeyStroke(32, true),
            '$' => new SimulatorTextKeyStroke(33, true),
            '%' => new SimulatorTextKeyStroke(34, true),
            '^' => new SimulatorTextKeyStroke(35, true),
            '&' => new SimulatorTextKeyStroke(36, true),
            '*' => new SimulatorTextKeyStroke(37, true),
            '(' => new SimulatorTextKeyStroke(38, true),
            ')' => new SimulatorTextKeyStroke(39, true),
            _ => default,
        };
        return stroke.UsageCode != 0;
    }
}
