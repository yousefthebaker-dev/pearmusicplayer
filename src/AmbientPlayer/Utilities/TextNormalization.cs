using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AmbientPlayer.Utilities;

/// <summary>
/// Normalises artist/album/track strings for fuzzy matching against iTunes
/// Search API results. Ported directly from artwork_watch.py - do not change
/// the pattern list or ordering without re-validating match rates against a
/// real listening session (see AMBIENT_PLAYER_SPEC.md section 5).
/// </summary>
public static partial class TextNormalization
{
    // Suffixes publishers add that stop titles matching cleanly.
    private static readonly Regex[] NoisePatterns =
    [
        NoiseParenRegex(),
        NoiseBracketRegex(),
        NoiseDashSuffixRegex(),
        FeatParenRegex(),
        FeatBracketRegex(),
        FeatTailRegex(),
        FtTailRegex(),
    ];

    public static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Decompose accents (NFKD) and drop the combining marks, so this
        // never throws on the heavy Unicode (stylised names, non-Latin
        // scripts) that shows up in a real library.
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c);
        }

        var result = sb.ToString().ToLowerInvariant().Replace("&", "and");

        foreach (var pattern in NoisePatterns)
        {
            result = pattern.Replace(result, " ");
        }

        result = NonAlphanumericRegex().Replace(result, " ");
        result = WhitespaceRegex().Replace(result, " ").Trim();
        return result;
    }

    [GeneratedRegex(@"\(.*?(deluxe|remaster|remastered|expanded|anniversary|edition|version|bonus|explicit|mono|stereo).*?\)")]
    private static partial Regex NoiseParenRegex();

    [GeneratedRegex(@"\[.*?(deluxe|remaster|remastered|expanded|anniversary|edition|version|bonus|explicit|mono|stereo).*?\]")]
    private static partial Regex NoiseBracketRegex();

    [GeneratedRegex(@"\s*-\s*(deluxe|remaster(ed)?|expanded|anniversary|single|ep)\b.*$")]
    private static partial Regex NoiseDashSuffixRegex();

    [GeneratedRegex(@"\(feat\..*?\)")]
    private static partial Regex FeatParenRegex();

    [GeneratedRegex(@"\[feat\..*?\]")]
    private static partial Regex FeatBracketRegex();

    [GeneratedRegex(@"\bfeat\..*$")]
    private static partial Regex FeatTailRegex();

    [GeneratedRegex(@"\bft\..*$")]
    private static partial Regex FtTailRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
