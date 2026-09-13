namespace AmbientPlayer.Models;

/// <summary>Mirrors the subset of SMTC playback state the UI cares about.</summary>
public enum PlaybackStatus
{
    Unknown,
    Playing,
    Paused,
    Stopped,
}

/// <summary>
/// A single poll's read of position/duration/status, timestamped so the UI
/// can interpolate between sparse SMTC timeline updates instead of binding
/// to them directly.
/// </summary>
public sealed record PlaybackSnapshot(TimeSpan Position, TimeSpan Duration, PlaybackStatus Status, DateTime ReadAtUtc)
{
    public static readonly PlaybackSnapshot Empty = new(TimeSpan.Zero, TimeSpan.Zero, PlaybackStatus.Unknown, DateTime.UtcNow);

    /// <summary>False for a zero/absent duration - the bar should be hidden, not drawn empty.</summary>
    public bool HasDuration => Duration > TimeSpan.Zero;
}
