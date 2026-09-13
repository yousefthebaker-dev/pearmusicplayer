namespace AmbientPlayer.Models;

public enum ArtworkSource
{
    /// <summary>High-res cover matched via the iTunes Search API.</summary>
    ITunes,

    /// <summary>The 125px SMTC thumbnail ShairportQt publishes - blurred, not upscaled.</summary>
    SmtcThumbnail,

    /// <summary>Nothing matched and no thumbnail was available.</summary>
    None,
}

/// <summary>
/// Result of resolving artwork for a track: where the image came from, its
/// local file path (null when <see cref="ArtworkSource.None"/>), and the
/// album name recovered from the iTunes match, if any - this is the only way
/// the app ever learns an album name, since ShairportQt does not send one.
/// </summary>
public sealed record ArtworkResult(ArtworkSource Source, string? LocalPath, string? ResolvedAlbum, double Confidence)
{
    public static readonly ArtworkResult None = new(ArtworkSource.None, null, null, 0.0);
}
