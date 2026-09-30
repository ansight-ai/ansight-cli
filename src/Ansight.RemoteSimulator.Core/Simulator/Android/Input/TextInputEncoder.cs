namespace Ansight.RemoteSimulator.Core.Simulator.Android.Input;

internal static class TextInputEncoder
{
    public static IReadOnlyList<TextInputOperation> Encode(string text)
    {
        var operations = new List<TextInputOperation>();
        var buffer = new System.Text.StringBuilder();
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (character == '\n')
            {
                FlushText(buffer, operations);
                operations.Add(new TextInputOperation(
                    TextInputOperationKind.KeyEvent,
                    "KEYCODE_ENTER"));
                continue;
            }

            if (character == '%')
            {
                FlushText(buffer, operations);
                operations.Add(new TextInputOperation(
                    TextInputOperationKind.Text,
                    "%"));
                continue;
            }

            if (character == ' ')
            {
                buffer.Append("%s");
            }
            else
            {
                buffer.Append(character);
            }
        }

        FlushText(buffer, operations);
        return operations;
    }

    private static void FlushText(
        System.Text.StringBuilder buffer,
        ICollection<TextInputOperation> operations)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        operations.Add(new TextInputOperation(
            TextInputOperationKind.Text,
            buffer.ToString()));
        buffer.Clear();
    }
}
