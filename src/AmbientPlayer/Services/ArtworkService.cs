using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AmbientPlayer.Models;
using AmbientPlayer.Utilities;

namespace AmbientPlayer.Services;

/// <summary>
/// Resolves display artwork for a track: iTunes Search API first, the SMTC
/// thumbnail as a blurred fallback, then nothing. Never touches SMTC itself -
/// the caller passes a delegate for the thumbnail fallback so this service
/// stays independently testable. See AMBIENT_PLAYER_SPEC.md section 5; the
/// lookup/scoring/caching logic here is a direct port of artwork_watch.py.
/// </summary>
public sealed partial class ArtworkService : IDisposable
{
    private const string Storefront = "GB";
    private const int ArtSize = 1000;
    private const int SearchLimit = 8;
    private const int CacheVersion = 1;
    private static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly string _artworkDir;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private DateTime _lastRequestUtc = DateTime.MinValue;
    private Dictionary<string, CacheEntry> _cache = new();
    private bool _cacheLoaded;

    public ArtworkService(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmbientPlayer");

        _artworkDir = Path.Combine(root, "artwork");
        Directory.CreateDirectory(_artworkDir);
        _cachePath = Path.Combine(_artworkDir, "cache.json");

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AmbientPlayer/1.0");
    }

    /// <summary>
    /// Resolves artwork for <paramref name="track"/>, following the cache ->
    /// iTunes -> SMTC-thumbnail -> none chain. <paramref name="thumbnailFallback"/>
    /// is only invoked when needed.
    /// </summary>
    public async Task<ArtworkResult> ResolveAsync(TrackInfo track, Func<Task<byte[]?>>? thumbnailFallback, CancellationToken token = default)
    {
        if (!track.HasTitle) return ArtworkResult.None;

        await EnsureCacheLoadedAsync().ConfigureAwait(false);

        var key = track.CacheKey;
        var cached = GetCached(key);
        if (cached is not null)
        {
            return await MaterializeCachedAsync(cached, thumbnailFallback, token).ConfigureAwait(false);
        }

        var outcome = await FindArtworkAsync(track, token).ConfigureAwait(false);

        if (outcome.ArtworkUrl is not null)
        {
            var localPath = BuildLocalPath(track);
            try
            {
                await DownloadAsync(outcome.ArtworkUrl, localPath, token).ConfigureAwait(false);
                await SaveCacheEntryAsync(key, new CacheEntry
                {
                    Url = outcome.ArtworkUrl,
                    File = localPath,
                    Confidence = outcome.Confidence,
                    Album = outcome.ResolvedAlbum,
                }).ConfigureAwait(false);

                return new ArtworkResult(ArtworkSource.ITunes, localPath, outcome.ResolvedAlbum, outcome.Confidence);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ArtworkService] download failed: {ex.Message}");
                // Don't cache a negative result here - the match was good,
                // only the download failed, and that's worth retrying.
            }
        }
        else
        {
            // Cache negative results too, so an unmatched track isn't
            // retried every play.
            await SaveCacheEntryAsync(key, new CacheEntry
            {
                Url = null,
                File = null,
                Confidence = outcome.Confidence,
                Album = null,
            }).ConfigureAwait(false);
        }

        return await FallbackToThumbnailAsync(thumbnailFallback, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _rateLimitGate.Dispose();
        _cacheLock.Dispose();
    }

    // ------------------------------------------------------------------
    // Lookup + scoring
    // ------------------------------------------------------------------

    private async Task<LookupOutcome> FindArtworkAsync(TrackInfo track, CancellationToken token)
    {
        var attempts = new List<(string Entity, string Term, string WantWork)>();

        if (track.HasArtist && track.HasAlbum)
        {
            attempts.Add(("album", $"{track.Artist} {track.Album}", track.Album));
        }
        if (track.HasAlbum && !track.HasArtist)
        {
            attempts.Add(("album", track.Album, track.Album));
        }
        // Song search is the fallback, and the only option when the sender
        // publishes no album name at all - which is what ShairportQt does,
        // so this is the path that actually runs in practice.
        if (track.HasArtist && track.HasTitle)
        {
            attempts.Add(("song", $"{track.Artist} {track.Title}", track.Title));
        }
        if (track.HasTitle && !track.HasArtist)
        {
            attempts.Add(("song", track.Title, track.Title));
        }

        string? bestUrl = null;
        var bestConfidence = 0.0;
        var bestReason = "no candidates";
        var bestAlbum = string.Empty;

        foreach (var (entity, term, wantWork) in attempts)
        {
            var results = await SearchAsync(term, entity, token).ConfigureAwait(false);
            foreach (var result in results)
            {
                var candidateArtist = result.ArtistName ?? string.Empty;
                var candidateAlbum = result.CollectionName ?? string.Empty;
                // Album search compares album to album, song search track to
                // track - comparing a track title against a collection name
                // rejected a correct match at 0.62 in the prototype.
                var candidateWork = entity == "album" ? candidateAlbum : result.TrackName ?? string.Empty;

                var confidence = MatchScoring.ScoreCandidate(track.Artist, wantWork, candidateArtist, candidateWork);
                if (confidence > bestConfidence)
                {
                    bestConfidence = confidence;
                    bestUrl = UpscaleArtworkUrl(result.ArtworkUrl100);
                    bestReason = $"{entity}: {candidateArtist} - {candidateWork}";
                    bestAlbum = candidateAlbum;
                }
            }

            if (bestConfidence >= MatchScoring.MatchThreshold) break;
        }

        if (bestConfidence < MatchScoring.MatchThreshold)
        {
            return new LookupOutcome(null, bestConfidence, $"best was {bestConfidence:F2} ({bestReason})", string.Empty);
        }

        return new LookupOutcome(bestUrl, bestConfidence, bestReason, bestAlbum);
    }

    private async Task<List<ITunesResult>> SearchAsync(string term, string entity, CancellationToken token)
    {
        var url = "https://itunes.apple.com/search"
            + $"?term={Uri.EscapeDataString(term)}"
            + $"&entity={entity}"
            + $"&limit={SearchLimit}"
            + $"&country={Storefront}";

        try
        {
            var json = await RateLimitedGetStringAsync(url, token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<ITunesSearchResponse>(json, JsonOptions);
            return response?.Results ?? [];
        }
        catch (Exception ex)
        {
            // iTunes returns 403 under aggressive polling; the rate gate
            // keeps well clear of that, but handle it rather than crash.
            Debug.WriteLine($"[ArtworkService] lookup failed: {ex.Message}");
            return [];
        }
    }

    private async Task<string> RateLimitedGetStringAsync(string url, CancellationToken token)
    {
        await _rateLimitGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wait = MinRequestGap - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            _lastRequestUtc = DateTime.UtcNow;

            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _rateLimitGate.Release();
        }
    }

    private async Task DownloadAsync(string url, string localPath, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, bytes, token).ConfigureAwait(false);
    }

    private static string? UpscaleArtworkUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return ArtworkSizeRegex().Replace(url, $"/{ArtSize}x{ArtSize}bb.jpg");
    }

    private string BuildLocalPath(TrackInfo track)
    {
        var name = $"{SafeName(track.Artist)}__{SafeName(track.HasAlbum ? track.Album : track.Title)}.jpg";
        return Path.Combine(_artworkDir, name);
    }

    private static string SafeName(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "unknown";
        var cleaned = UnsafeFileCharsRegex().Replace(text, "_");
        if (cleaned.Length > 70) cleaned = cleaned[..70];
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    // ------------------------------------------------------------------
    // Cache
    // ------------------------------------------------------------------

    private async Task EnsureCacheLoadedAsync()
    {
        if (_cacheLoaded) return;
        await _cacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheLoaded) return;
            _cache = await LoadCacheAsync().ConfigureAwait(false);
            _cacheLoaded = true;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<Dictionary<string, CacheEntry>> LoadCacheAsync()
    {
        if (!File.Exists(_cachePath)) return new Dictionary<string, CacheEntry>();

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<CacheFile>(json, JsonOptions);
            if (file is null || file.Version != CacheVersion)
            {
                // Matching logic changed since this cache was written -
                // discard wholesale rather than leave stale verdicts mixed
                // in with new ones.
                return new Dictionary<string, CacheEntry>();
            }
            return file.Entries;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkService] cache load failed, starting fresh: {ex.Message}");
            return new Dictionary<string, CacheEntry>();
        }
    }

    private CacheEntry? GetCached(string key) => _cache.TryGetValue(key, out var entry) ? entry : null;

    private async Task SaveCacheEntryAsync(string key, CacheEntry entry)
    {
        await _cacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache[key] = entry;
            var file = new CacheFile { Version = CacheVersion, Entries = _cache };
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await File.WriteAllTextAsync(_cachePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkService] cache save failed: {ex.Message}");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<ArtworkResult> MaterializeCachedAsync(CacheEntry entry, Func<Task<byte[]?>>? thumbnailFallback, CancellationToken token)
    {
        if (entry.Url is null || entry.File is null)
        {
            // Cached negative result - don't retry the search, just fall back.
            return await FallbackToThumbnailAsync(thumbnailFallback, token).ConfigureAwait(false);
        }

        if (File.Exists(entry.File))
        {
            return new ArtworkResult(ArtworkSource.ITunes, entry.File, entry.Album, entry.Confidence);
        }

        // The image file went missing (cache dir cleared, etc). Re-fetch the
        // bytes from the already-known URL rather than re-running the search
        // - restarting the app must not cost a fresh API call.
        try
        {
            await DownloadAsync(entry.Url, entry.File, token).ConfigureAwait(false);
            return new ArtworkResult(ArtworkSource.ITunes, entry.File, entry.Album, entry.Confidence);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkService] re-download of cached artwork failed: {ex.Message}");
            return await FallbackToThumbnailAsync(thumbnailFallback, token).ConfigureAwait(false);
        }
    }

    private async Task<ArtworkResult> FallbackToThumbnailAsync(Func<Task<byte[]?>>? thumbnailFallback, CancellationToken token)
    {
        if (thumbnailFallback is null) return ArtworkResult.None;

        byte[]? bytes;
        try
        {
            bytes = await thumbnailFallback().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkService] thumbnail fallback failed: {ex.Message}");
            return ArtworkResult.None;
        }

        if (bytes is null || bytes.Length == 0) return ArtworkResult.None;

        try
        {
            var path = Path.Combine(_artworkDir, "_smtc_thumbnail.jpg");
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            return new ArtworkResult(ArtworkSource.SmtcThumbnail, path, null, 0.0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtworkService] writing thumbnail fallback failed: {ex.Message}");
            return ArtworkResult.None;
        }
    }

    [GeneratedRegex(@"/\d+x\d+bb\.(jpg|png)")]
    private static partial Regex ArtworkSizeRegex();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeFileCharsRegex();

    private readonly record struct LookupOutcome(string? ArtworkUrl, double Confidence, string Reason, string ResolvedAlbum);

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, CacheEntry> Entries { get; set; } = new();
    }

    private sealed class CacheEntry
    {
        public string? Url { get; set; }
        public string? File { get; set; }
        public double Confidence { get; set; }
        public string? Album { get; set; }
    }

    private sealed class ITunesSearchResponse
    {
        public List<ITunesResult> Results { get; set; } = [];
    }

    private sealed class ITunesResult
    {
        public string? ArtistName { get; set; }
        public string? CollectionName { get; set; }
        public string? TrackName { get; set; }
        public string? ArtworkUrl100 { get; set; }
    }
}
