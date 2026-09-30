namespace Ansight.Host.Runtime.Automation;

internal static class RepositoryModuleDescriptorReader
{
    public static string ExtractObject(
        string source,
        string modulePath,
        string exportName,
        string moduleKind)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKind);

        var descriptorEndIndex = FindExportedConstDescriptor(source, exportName);
        if (descriptorEndIndex < 0)
        {
            throw new InvalidDataException(
                $"{moduleKind} module '{modulePath}' must export a JSON-compatible '{exportName}' descriptor.");
        }

        var equalsIndex = source.IndexOf('=', descriptorEndIndex);
        if (equalsIndex < 0)
        {
            throw new InvalidDataException(
                $"{moduleKind} module '{modulePath}' descriptor is missing '='.");
        }

        var objectStart = equalsIndex + 1;
        while (objectStart < source.Length && char.IsWhiteSpace(source[objectStart]))
        {
            objectStart++;
        }

        if (objectStart >= source.Length || source[objectStart] != '{')
        {
            throw new InvalidDataException(
                $"{moduleKind} module '{modulePath}' descriptor must be a JSON object literal.");
        }

        var depth = 0;
        var isInString = false;
        var isEscaped = false;
        for (var index = objectStart; index < source.Length; index++)
        {
            var character = source[index];
            if (isInString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (character == '\\')
                {
                    isEscaped = true;
                }
                else if (character == '"')
                {
                    isInString = false;
                }

                continue;
            }

            if (character == '"')
            {
                isInString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return source[objectStart..(index + 1)];
            }
        }

        throw new InvalidDataException(
            $"{moduleKind} module '{modulePath}' descriptor is not closed.");
    }

    public static bool ContainsExportedDescriptor(string source, string exportName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        return FindExportedConstDescriptor(source, exportName) >= 0;
    }

    private static int FindExportedConstDescriptor(string source, string exportName)
    {
        var index = 0;
        var state = 0;
        while (index < source.Length)
        {
            SkipTrivia(source, ref index);
            if (index >= source.Length)
            {
                break;
            }

            if (source[index] is '\'' or '"' or '`')
            {
                SkipQuotedValue(source, ref index, source[index]);
                state = 0;
                continue;
            }

            if (!IsIdentifierStart(source[index]))
            {
                index++;
                state = 0;
                continue;
            }

            var identifierStart = index++;
            while (index < source.Length && IsIdentifierCharacter(source[index]))
            {
                index++;
            }

            var identifier = source.AsSpan(identifierStart, index - identifierStart);
            if (state == 0)
            {
                state = identifier.SequenceEqual("export") ? 1 : 0;
            }
            else if (state == 1)
            {
                state = identifier.SequenceEqual("const") ? 2 : identifier.SequenceEqual("export") ? 1 : 0;
            }
            else if (identifier.SequenceEqual(exportName))
            {
                return index;
            }
            else
            {
                state = identifier.SequenceEqual("export") ? 1 : 0;
            }
        }

        return -1;
    }

    private static void SkipTrivia(string source, ref int index)
    {
        while (index < source.Length)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (index + 1 >= source.Length || source[index] != '/')
            {
                return;
            }

            if (source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not '\r' and not '\n')
                {
                    index++;
                }
                continue;
            }

            if (source[index + 1] != '*')
            {
                return;
            }

            index += 2;
            while (index + 1 < source.Length
                   && (source[index] != '*' || source[index + 1] != '/'))
            {
                index++;
            }
            index = Math.Min(source.Length, index + 2);
        }
    }

    private static void SkipQuotedValue(string source, ref int index, char delimiter)
    {
        index++;
        var isEscaped = false;
        while (index < source.Length)
        {
            var character = source[index++];
            if (isEscaped)
            {
                isEscaped = false;
            }
            else if (character == '\\')
            {
                isEscaped = true;
            }
            else if (character == delimiter)
            {
                return;
            }
        }
    }

    private static bool IsIdentifierStart(char value)
        => char.IsLetter(value) || value is '_' or '$';

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$';
}
