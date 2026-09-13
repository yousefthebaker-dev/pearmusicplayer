namespace AmbientPlayer.Utilities;

/// <summary>
/// Cleans raw SMTC metadata fields. ShairportQt (and senders generally) will
/// publish a whitespace-filled field rather than omitting it, and a run of
/// spaces is truthy in a plain null/empty check - which is enough to fire a
/// lookup against junk and poison the artwork cache. This was the single
/// biggest source of wasted API calls while prototyping this app.
/// </summary>
public static class MetadataCleaner
{
    private static readonly char[] ZeroWidthChars = ['​', '‌', '‍', '﻿'];

    /// <summary>
    /// Null becomes empty. Ordinary whitespace and zero-width characters are
    /// trimmed from both ends, repeatedly, since the two can be interleaved
    /// (e.g. a zero-width space wrapped in ordinary spaces). Anything left
    /// blank after that is treated as genuinely absent.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var trimmed = value;
        string previous;
        do
        {
            previous = trimmed;
            trimmed = trimmed.Trim();
            trimmed = trimmed.Trim(ZeroWidthChars);
        } while (trimmed != previous);

        return trimmed;
    }
}
