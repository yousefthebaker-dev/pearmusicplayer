using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AmbientPlayer.Native;

/// <summary>Hides the mouse cursor over a window after a period of inactivity, restoring it on movement.</summary>
public sealed class CursorAutoHide
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;

    public CursorAutoHide(Window window, TimeSpan? idleDelay = null)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = idleDelay ?? TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _window.Cursor = Cursors.None;
        };

        _window.PreviewMouseMove += (_, _) => Reset();
        _window.PreviewMouseDown += (_, _) => Reset();
        Reset();
    }

    private void Reset()
    {
        _window.Cursor = Cursors.Arrow;
        _timer.Stop();
        _timer.Start();
    }
}
