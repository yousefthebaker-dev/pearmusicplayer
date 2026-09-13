using AmbientPlayer.Models;

namespace AmbientPlayer.Rendering;

/// <summary>
/// Smooths SMTC's sparse, irregular timeline updates into steady per-frame
/// progress instead of a bar that jumps. See AMBIENT_PLAYER_SPEC.md section 8.
/// </summary>
public sealed class ProgressInterpolator
{
    private const double ResyncThresholdSeconds = 1.5;

    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;

    public bool HasDuration => _snapshot.HasDuration;
    public TimeSpan Duration => _snapshot.Duration;

    /// <summary>
    /// Feed a fresh SMTC read in. Resyncs to it only if it disagrees with
    /// where local interpolation had already put us by more than the
    /// threshold, so ordinary polling jitter doesn't cause visible stutter.
    /// </summary>
    public void Update(PlaybackSnapshot snapshot)
    {
        var interpolatedAtReadTime = GetPosition(snapshot.ReadAtUtc);
        var drift = Math.Abs((snapshot.Position - interpolatedAtReadTime).TotalSeconds);

        if (_snapshot.Status == PlaybackStatus.Unknown || drift > ResyncThresholdSeconds)
        {
            _snapshot = snapshot;
        }
        else
        {
            // Small disagreement - keep interpolating from the existing
            // anchor, but still adopt duration/status in case those changed.
            _snapshot = _snapshot with { Duration = snapshot.Duration, Status = snapshot.Status };
        }
    }

    /// <summary>Interpolated elapsed position as of <paramref name="nowUtc"/>, frozen while not playing.</summary>
    public TimeSpan GetPosition(DateTime nowUtc)
    {
        if (_snapshot.Status != PlaybackStatus.Playing)
        {
            return Clamp(_snapshot.Position);
        }

        var elapsedSincePoll = nowUtc - _snapshot.ReadAtUtc;
        if (elapsedSincePoll < TimeSpan.Zero) elapsedSincePoll = TimeSpan.Zero;
        return Clamp(_snapshot.Position + elapsedSincePoll);
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        if (_snapshot.HasDuration && position > _snapshot.Duration) return _snapshot.Duration;
        return position;
    }
}
