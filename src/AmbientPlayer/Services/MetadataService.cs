using System.Diagnostics;
using System.Threading;
using AmbientPlayer.Models;
using AmbientPlayer.Utilities;
using Windows.Foundation;
using Windows.Media.Control;

namespace AmbientPlayer.Services;

/// <summary>
/// Polls Windows SMTC for the ShairportQt session (or whichever session is
/// current, as a fallback) and raises settled track changes plus periodic
/// playback snapshots. Never touches the iTunes API or WPF directly - see
/// AMBIENT_PLAYER_SPEC.md section 4.
/// </summary>
public sealed class MetadataService : IAsyncDisposable
{
    private readonly string _appFilter;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _settleDelay;
    private readonly CancellationTokenSource _cts = new();

    private Task? _pollLoop;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _trackedSession;
    private string? _trackedAppId;
    private string? _lastEmittedKey;

    /// <summary>Raised once a track's metadata has settled (read twice, 1.5s apart, identically).</summary>
    public event EventHandler<TrackInfo>? TrackChanged;

    /// <summary>Raised on every poll with the latest position/duration/status.</summary>
    public event EventHandler<PlaybackSnapshot>? PlaybackUpdated;

    /// <summary>Raised when the tracked session's app id changes (including to null) - surfaced for debugging.</summary>
    public event EventHandler<string?>? TrackedSessionChanged;

    public MetadataService(string appFilter = "ShairportQt", TimeSpan? pollInterval = null, TimeSpan? settleDelay = null)
    {
        _appFilter = appFilter;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _settleDelay = settleDelay ?? TimeSpan.FromSeconds(1.5);
    }

    /// <summary>The app id of the session currently being tracked, or null when none is available.</summary>
    public string? TrackedAppId => _trackedAppId;

    public void Start()
    {
        _pollLoop ??= Task.Run(() => PollLoopAsync(_cts.Token));
    }

    // These control the sending iPhone via SMTC's transport commands, not
    // local playback, and will fail (return false) whenever nothing is
    // connected - callers should disable rather than hide the buttons.
    public Task<bool> TryPlayPauseAsync() => RunTransportCommand(s => s.TryTogglePlayPauseAsync());

    public Task<bool> TrySkipNextAsync() => RunTransportCommand(s => s.TrySkipNextAsync());

    public Task<bool> TrySkipPreviousAsync() => RunTransportCommand(s => s.TrySkipPreviousAsync());

    /// <summary>Reads the tracked session's current thumbnail, for the SMTC-thumbnail artwork fallback.</summary>
    public async Task<byte[]?> GetCurrentThumbnailAsync()
    {
        var session = _trackedSession;
        if (session is null) return null;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            return await StreamReferenceReader.ReadAllBytesAsync(props.Thumbnail);
        }
        catch (Exception ex)
        {
            // TryGetMediaPropertiesAsync throws intermittently during session
            // teardown - this is expected and not fatal.
            Debug.WriteLine($"[MetadataService] thumbnail read failed: {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pollLoop is not null)
        {
            try { await _pollLoop; }
            catch (OperationCanceledException) { /* expected */ }
        }
        _cts.Dispose();
    }

    private async Task<bool> RunTransportCommand(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        var session = _trackedSession;
        if (session is null) return false;

        try
        {
            return await command(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MetadataService] transport command failed: {ex.Message}");
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single bad poll (session teardown, AirPlay dropping mid-
                // read, etc.) should never take the whole loop down - the app
                // is meant to idle quietly and pick back up, not crash.
                Debug.WriteLine($"[MetadataService] poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        var session = SelectSession(_manager);
        if (session is null)
        {
            _trackedSession = null;
            if (_trackedAppId is not null)
            {
                _trackedAppId = null;
                TrackedSessionChanged?.Invoke(this, null);
            }
            return;
        }

        _trackedSession = session;
        var appId = session.SourceAppUserModelId;
        if (appId != _trackedAppId)
        {
            _trackedAppId = appId;
            TrackedSessionChanged?.Invoke(this, appId);
        }

        // Playback/timeline are read independently of metadata so the
        // progress bar keeps moving even if a metadata read fails.
        EmitPlaybackSnapshot(session);

        TrackInfo current;
        try
        {
            current = await ReadTrackAsync(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MetadataService] media properties read failed: {ex.Message}");
            return;
        }

        if (!current.HasTitle) return;
        if (current.CacheKey == _lastEmittedKey) return;

        // Senders publish metadata field-by-field, so the first read after a
        // change is often half filled. Wait, re-read, and only act once the
        // same values come back twice - otherwise every track change fires
        // two lookups, one of them against junk.
        await Task.Delay(_settleDelay, token).ConfigureAwait(false);

        TrackInfo settled;
        try
        {
            settled = await ReadTrackAsync(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MetadataService] settle read failed: {ex.Message}");
            return;
        }

        if (settled != current)
        {
            // Still changing - let the next poll pick up wherever it lands.
            return;
        }

        _lastEmittedKey = settled.CacheKey;
        TrackChanged?.Invoke(this, settled);
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = manager.GetSessions();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MetadataService] GetSessions failed: {ex.Message}");
            return null;
        }

        if (!string.IsNullOrEmpty(_appFilter))
        {
            foreach (var candidate in sessions)
            {
                var id = candidate.SourceAppUserModelId ?? string.Empty;
                if (id.Contains(_appFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        try
        {
            return manager.GetCurrentSession();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MetadataService] GetCurrentSession failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<TrackInfo> ReadTrackAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();

        // Artist resolution order: AlbumArtist, then Artist.
        var artist = MetadataCleaner.Clean(props.AlbumArtist);
        if (artist.Length == 0)
        {
            artist = MetadataCleaner.Clean(props.Artist);
        }

        var album = MetadataCleaner.Clean(props.AlbumTitle);
        var title = MetadataCleaner.Clean(props.Title);
        return new TrackInfo(artist, album, title);
    }

    private void EmitPlaybackSnapshot(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var status = MapStatus(playback?.PlaybackStatus);
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            var snapshot = new PlaybackSnapshot(timeline.Position, duration, status, DateTime.UtcNow);
            PlaybackUpdated?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MetadataService] playback/timeline read failed: {ex.Message}");
        }
    }

    private static PlaybackStatus MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackStatus.Stopped,
        _ => PlaybackStatus.Unknown,
    };
}
