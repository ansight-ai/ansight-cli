using System.Globalization;
using System.Text;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiFuzzyStringMatcher
{
    private const double MinimumSimilarity = 0.8;

    public static LiveUiStringMatchResult Evaluate(
        string actual,
        string expected,
        bool caseSensitive)
    {
        var normalizedActual = Normalize(actual, caseSensitive);
        var normalizedExpected = Normalize(expected, caseSensitive);
        if (normalizedActual.Length == 0 || normalizedExpected.Length == 0)
        {
            return LiveUiStringMatchResult.NoMatch;
        }

        if (string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal))
        {
            return new LiveUiStringMatchResult(true, 1, "normalized-exact");
        }

        if (normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            var coverage = (double)normalizedExpected.Length / normalizedActual.Length;
            return new LiveUiStringMatchResult(
                true,
                Math.Round(0.96 + (0.04 * coverage), 3),
                "normalized-contains");
        }

        var bestMatch = EvaluateEditDistance(normalizedActual, normalizedExpected, "edit-distance");
        var expectedTokens = SplitTokens(normalizedExpected);
        var actualTokens = SplitTokens(normalizedActual);
        if (expectedTokens.Length == 1)
        {
            foreach (var actualToken in actualTokens)
            {
                bestMatch = Better(
                    bestMatch,
                    EvaluateEditDistance(actualToken, expectedTokens[0], "token-edit-distance"));
            }
        }
        else if (expectedTokens.Length > 1)
        {
            bestMatch = Better(bestMatch, EvaluateTokenCoverage(actualTokens, expectedTokens));
        }

        return bestMatch;
    }

    private static LiveUiStringMatchResult EvaluateTokenCoverage(
        IReadOnlyList<string> actualTokens,
        IReadOnlyList<string> expectedTokens)
    {
        if (actualTokens.Count == 0 || actualTokens.Count < expectedTokens.Count)
        {
            return LiveUiStringMatchResult.NoMatch;
        }

        var usedActualTokens = new bool[actualTokens.Count];
        var scoreTotal = 0d;
        var editDistanceTotal = 0;
        foreach (var expectedToken in expectedTokens)
        {
            var bestTokenIndex = -1;
            var bestTokenMatch = LiveUiStringMatchResult.NoMatch;
            for (var index = 0; index < actualTokens.Count; index++)
            {
                if (usedActualTokens[index])
                {
                    continue;
                }

                var candidate = string.Equals(actualTokens[index], expectedToken, StringComparison.Ordinal)
                    ? new LiveUiStringMatchResult(true, 1, "token-exact")
                    : EvaluateEditDistance(actualTokens[index], expectedToken, "token-edit-distance");
                if (candidate.IsMatch && candidate.Score > bestTokenMatch.Score)
                {
                    bestTokenIndex = index;
                    bestTokenMatch = candidate;
                }
            }

            if (bestTokenIndex < 0)
            {
                return LiveUiStringMatchResult.NoMatch;
            }

            usedActualTokens[bestTokenIndex] = true;
            scoreTotal += bestTokenMatch.Score;
            editDistanceTotal += ReadTrailingInteger(bestTokenMatch.Reason);
        }

        var averageScore = scoreTotal / expectedTokens.Count;
        var coveragePenalty = 0.9
                              + (0.1 * Math.Min(1, (double)expectedTokens.Count / actualTokens.Count));
        return new LiveUiStringMatchResult(
            true,
            Math.Round(averageScore * coveragePenalty, 3),
            editDistanceTotal == 0
                ? "token-set"
                : $"token-set-edit-distance-{editDistanceTotal}");
    }

    private static LiveUiStringMatchResult EvaluateEditDistance(
        string actual,
        string expected,
        string reasonPrefix)
    {
        var comparisonLength = Math.Max(actual.Length, expected.Length);
        if (Math.Min(actual.Length, expected.Length) <= 3)
        {
            return LiveUiStringMatchResult.NoMatch;
        }

        var maximumDistance = comparisonLength switch
        {
            <= 7 => 1,
            <= 14 => 2,
            _ => Math.Max(3, (int)Math.Floor(comparisonLength * 0.15))
        };
        if (Math.Abs(actual.Length - expected.Length) > maximumDistance)
        {
            return LiveUiStringMatchResult.NoMatch;
        }

        var distance = CalculateDamerauLevenshteinDistance(actual, expected, maximumDistance);
        var similarity = 1d - ((double)distance / comparisonLength);
        return distance <= maximumDistance && similarity >= MinimumSimilarity
            ? new LiveUiStringMatchResult(
                true,
                Math.Round(similarity, 3),
                $"{reasonPrefix}-{distance}")
            : LiveUiStringMatchResult.NoMatch;
    }

    private static int CalculateDamerauLevenshteinDistance(
        string left,
        string right,
        int maximumDistance)
    {
        var previousPreviousRow = new int[right.Length + 1];
        var previousRow = Enumerable.Range(0, right.Length + 1).ToArray();
        var currentRow = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            currentRow[0] = leftIndex;
            var rowMinimum = currentRow[0];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(
                        previousRow[rightIndex] + 1,
                        currentRow[rightIndex - 1] + 1),
                    previousRow[rightIndex - 1] + substitutionCost);
                if (leftIndex > 1
                    && rightIndex > 1
                    && left[leftIndex - 1] == right[rightIndex - 2]
                    && left[leftIndex - 2] == right[rightIndex - 1])
                {
                    value = Math.Min(value, previousPreviousRow[rightIndex - 2] + 1);
                }

                currentRow[rightIndex] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }

            if (rowMinimum > maximumDistance)
            {
                return maximumDistance + 1;
            }

            var reusableRow = previousPreviousRow;
            previousPreviousRow = previousRow;
            previousRow = currentRow;
            currentRow = reusableRow;
        }

        return previousRow[right.Length];
    }

    private static string Normalize(string value, bool caseSensitive)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;
        var previousWasLowercase = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (!char.IsLetterOrDigit(character))
            {
                pendingSeparator = result.Length > 0;
                previousWasLowercase = false;
                continue;
            }

            var beginsCamelCaseWord = previousWasLowercase && char.IsUpper(character);
            if ((pendingSeparator || beginsCamelCaseWord)
                && result.Length > 0
                && result[^1] != ' ')
            {
                result.Append(' ');
            }

            result.Append(caseSensitive ? character : char.ToLowerInvariant(character));
            pendingSeparator = false;
            previousWasLowercase = char.IsLower(character);
        }

        return result.ToString().Trim();
    }

    private static string[] SplitTokens(string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static LiveUiStringMatchResult Better(
        LiveUiStringMatchResult left,
        LiveUiStringMatchResult right)
        => right.Score > left.Score ? right : left;

    private static int ReadTrailingInteger(string reason)
    {
        var separatorIndex = reason.LastIndexOf("-", StringComparison.Ordinal);
        return separatorIndex >= 0
               && int.TryParse(reason[(separatorIndex + 1)..], out var value)
            ? value
            : 0;
    }
}

internal sealed record LiveUiStringMatchResult(
    bool IsMatch,
    double Score,
    string Reason)
{
    public static LiveUiStringMatchResult NoMatch { get; } = new(false, 0, "no-match");
}
