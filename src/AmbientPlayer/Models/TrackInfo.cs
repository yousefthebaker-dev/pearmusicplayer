namespace AmbientPlayer.Models;

/// <summary>
/// Cleaned, settled metadata for the currently playing track. Every field has
/// already been through <see cref="Utilities.MetadataCleaner"/>, so an empty
/// string here means "the sender did not give us this", never whitespace.
/// </summary>
public sealed record TrackInfo(string Artist, string Album, string Title)
{
    public static readonly TrackInfo Empty = new(string.Empty, string.Empty, string.Empty);

    public bool HasArtist => Artist.Length > 0;
    public bool HasAlbum => Album.Length > 0;
    public bool HasTitle => Title.Length > 0;

    /// <summary>
    /// Cache/dedupe key. ShairportQt never publishes an album name, so most
    /// tracks fall back to keying on the track title instead - there is
    /// nothing else stable to key on.
    /// </summary>
    public string CacheKey => HasAlbum
        ? $"{Artist}|||{Album}"
        : $"{Artist}|||track:{Title}";
}
