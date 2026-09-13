namespace AmbientPlayer.Utilities;

/// <summary>
/// Confidence scoring for iTunes Search API candidates. Ported from
/// artwork_watch.py - see AMBIENT_PLAYER_SPEC.md section 5. Getting this
/// wrong produces confidently wrong album art, which is worse than no art
/// because it also poisons the background palette - so this is ported
/// exactly rather than "improved".
/// </summary>
public static class MatchScoring
{
    public const double MatchThreshold = 0.72;

    public static double Similarity(string? a, string? b)
    {
        var normA = TextNormalization.Normalise(a);
        var normB = TextNormalization.Normalise(b);
        if (normA.Length == 0 || normB.Length == 0) return 0.0;
        if (normA == normB) return 1.0;

        // One containing the other is a strong signal (e.g. "paths" in "paths ep").
        if (normA.Contains(normB, StringComparison.Ordinal) || normB.Contains(normA, StringComparison.Ordinal))
        {
            return 0.92;
        }

        return SequenceRatio.Ratio(normA, normB);
    }

    /// <summary>
    /// Weighted confidence that a search result is the track/album we asked
    /// for. "work" is the album title on an album search and the track title
    /// on a song search - compare like with like, or a correct hit scores as
    /// a miss (comparing a track title against a collection name rejected a
    /// correct match at 0.62 in the prototype).
    /// </summary>
    public static double ScoreCandidate(string? wantArtist, string? wantWork, string? candidateArtist, string? candidateWork)
    {
        var artistScore = Similarity(wantArtist, candidateArtist);
        var workScore = Similarity(wantWork, candidateWork);

        // Artist matters slightly more - a wrong artist is always wrong,
        // whereas titles get mangled by editions, reissues and remixes.
        return artistScore * 0.55 + workScore * 0.45;
    }
}
