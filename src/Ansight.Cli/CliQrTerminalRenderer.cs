using System.Text;
using Ansight.Host.Qr;

namespace Ansight.Cli;

internal static class CliQrTerminalRenderer
{
    private const string AnsiBlackOnWhite = "\u001b[30;47m";
    private const string AnsiReset = "\u001b[0m";

    public static string Render(string payload, bool useAnsiColors = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(payload, ECCLevel.M);
        var matrix = qrData.ModuleMatrix;
        var result = new StringBuilder();

        for (var row = 0; row < matrix.Count; row += 2)
        {
            if (useAnsiColors)
            {
                result.Append(AnsiBlackOnWhite);
            }

            for (var column = 0; column < matrix[row].Length; column++)
            {
                var top = matrix[row][column];
                var bottom = row + 1 < matrix.Count && matrix[row + 1][column];
                result.Append((top, bottom) switch
                {
                    (true, true) => '\u2588',
                    (true, false) => '\u2580',
                    (false, true) => '\u2584',
                    _ => ' '
                });
            }

            if (useAnsiColors)
            {
                result.Append(AnsiReset);
            }

            if (row + 2 < matrix.Count)
            {
                result.AppendLine();
            }
        }

        return result.ToString();
    }
}
