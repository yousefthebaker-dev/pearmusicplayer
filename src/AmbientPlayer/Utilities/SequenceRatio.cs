namespace AmbientPlayer.Utilities;

/// <summary>
/// A from-scratch port of Python's <c>difflib.SequenceMatcher.ratio()</c>
/// (Ratcliff/Obershelp gestalt pattern matching), used as the fallback
/// similarity measure once exact-match and containment checks are ruled out.
/// artwork_watch.py relies on this exact algorithm - re-implementing it as
/// plain Levenshtein distance would shift scores around the 0.72 threshold
/// and change which candidates match.
/// </summary>
public static class SequenceRatio
{
    public static double Ratio(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length == 0 && b.Length == 0) return 1.0;

        int matches = CountMatches(a, 0, a.Length, b, 0, b.Length);
        return 2.0 * matches / (a.Length + b.Length);
    }

    private static int CountMatches(string a, int aLo, int aHi, string b, int bLo, int bHi)
    {
        var (matchA, matchB, size) = FindLongestMatch(a, aLo, aHi, b, bLo, bHi);
        if (size == 0) return 0;

        var total = size;
        total += CountMatches(a, aLo, matchA, b, bLo, matchB);
        total += CountMatches(a, matchA + size, aHi, b, matchB + size, bHi);
        return total;
    }

    /// <summary>Longest common substring within a[aLo,aHi) and b[bLo,bHi), via plain O(n*m) DP.</summary>
    private static (int aIndex, int bIndex, int length) FindLongestMatch(string a, int aLo, int aHi, string b, int bLo, int bHi)
    {
        int aLen = aHi - aLo;
        int bLen = bHi - bLo;
        if (aLen <= 0 || bLen <= 0) return (aLo, bLo, 0);

        var prevRow = new int[bLen + 1];
        var currRow = new int[bLen + 1];
        int bestLen = 0, bestA = aLo, bestB = bLo;

        for (var i = 1; i <= aLen; i++)
        {
            for (var j = 1; j <= bLen; j++)
            {
                if (a[aLo + i - 1] == b[bLo + j - 1])
                {
                    currRow[j] = prevRow[j - 1] + 1;
                    if (currRow[j] > bestLen)
                    {
                        bestLen = currRow[j];
                        bestA = aLo + i - bestLen;
                        bestB = bLo + j - bestLen;
                    }
                }
                else
                {
                    currRow[j] = 0;
                }
            }

            (prevRow, currRow) = (currRow, prevRow);
            Array.Clear(currRow);
        }

        return (bestA, bestB, bestLen);
    }
}
