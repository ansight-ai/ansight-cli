using System.Collections.Generic;

namespace Ansight.Infrastructure.Utilities;

public static class ListComparisonHelper
{
    public static bool ItemsMatch(IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var t in right)
        {
            var hasMatch = false;
            foreach (var t1 in left)
            {
                if (string.Equals(t, t1, StringComparison.Ordinal))
                {
                    hasMatch = true;
                    break;
                }
            }

            if (!hasMatch)
            {
                return false;
            }
        }

        return true;
    }
}
