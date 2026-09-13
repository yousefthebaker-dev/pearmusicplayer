using System.Runtime.InteropServices;

namespace AmbientPlayer.Native;

/// <summary>
/// Wraps <c>SetThreadExecutionState</c> so the display doesn't blank mid-
/// album. See AMBIENT_PLAYER_SPEC.md section 9. Call <see cref="Keep"/> on
/// startup and <see cref="Release"/> (or dispose) on exit - forgetting the
/// release leaves the flag set for the process's lifetime, not forever, but
/// there's no reason to rely on that.
/// </summary>
public sealed class DisplayRequest : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;

    private bool _active;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public void Keep()
    {
        SetThreadExecutionState(EsContinuous | EsDisplayRequired);
        _active = true;
    }

    public void Release()
    {
        if (!_active) return;
        SetThreadExecutionState(EsContinuous);
        _active = false;
    }

    public void Dispose() => Release();
}
