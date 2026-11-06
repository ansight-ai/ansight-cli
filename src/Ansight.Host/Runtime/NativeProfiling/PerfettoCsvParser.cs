using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Runtime.NativeProfiling;

internal static class PerfettoCsvParser
{
    public static PerfettoCsvTable Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new PerfettoCsvTable([], []);
        }

        var records = ParseRecords(csv);

        if (records.Count == 0)
        {
            return new PerfettoCsvTable([], []);
        }

        var columns = records[0].Select(static value => value ?? string.Empty).ToArray();
        var rows = records.Skip(1)
            .Where(record => record.Count > 1 || !string.IsNullOrEmpty(record[0]))
            .Select(record => (IReadOnlyList<string?>)NormalizeRecord(record, columns.Length))
            .ToArray();

        return new PerfettoCsvTable(columns, rows);
    }

    private static IReadOnlyList<List<string?>> ParseRecords(string csv)
    {
        var records = new List<List<string?>>();
        var record = new List<string?>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < csv.Length && csv[index + 1] == '\n')
                    {
                        index++;
                    }
                    CompleteRecord(records, record, field);
                    break;
                case '\n':
                    CompleteRecord(records, record, field);
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            CompleteRecord(records, record, field);
        }

        return records;
    }

    private static void CompleteRecord(
        ICollection<List<string?>> records,
        List<string?> record,
        StringBuilder field)
    {
        record.Add(field.ToString());
        records.Add([.. record]);
        record.Clear();
        field.Clear();
    }

    private static IReadOnlyList<string?> NormalizeRecord(IReadOnlyList<string?> record, int columnCount)
    {
        var normalized = new string?[columnCount];

        for (var index = 0; index < normalized.Length && index < record.Count; index++)
        {
            normalized[index] = string.Equals(record[index], "[NULL]", StringComparison.Ordinal)
                ? null
                : record[index];
        }

        return normalized;
    }
}
