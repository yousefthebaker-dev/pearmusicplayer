using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using AmbientPlayer.Models;
using AmbientPlayer.Native;
using AmbientPlayer.Rendering;
using AmbientPlayer.Services;

namespace AmbientPlayer;

/// <summary>
/// Renders everything. Never calls the iTunes API or touches SMTC directly -
/// it only talks to <see cref="MetadataService"/>, <see cref="ArtworkService"/>
/// and <see cref="PaletteService"/>. See AMBIENT_PLAYER_SPEC.md sections 3, 7-9.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata;
    private readonly ArtworkService _artwork;
    private readonly PaletteService _palette;
    private readonly ProgressInterpolator _progress = new();
    private readonly DisplayRequest _displayRequest = new();

    private CursorAutoHide? _cursorAutoHide;
    private CancellationTokenSource? _artworkCts;
    private bool _artworkLayerAIsFront = true;
    private bool _isFullscreen = true;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _metadata = new MetadataService();
        _artwork = new ArtworkService();
        _palette = new PaletteService();

        _metadata.TrackChanged += OnTrackChanged;
        _metadata.PlaybackUpdated += OnPlaybackUpdated;
        _metadata.TrackedSessionChanged += OnTrackedSessionChanged;
    }

    // ------------------------------------------------------------------
    // Window lifecycle
    // ------------------------------------------------------------------

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnRememberedMonitor(); // also sets WindowState = Maximized

        ApplyProportionalLayout();
        MeshBackground.SetPalette(PaletteService.DefaultPalette());

        PrevButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextButton.IsEnabled = false;

        _displayRequest.Keep();
        _cursorAutoHide = new CursorAutoHide(this);

        CompositionTarget.Rendering += OnRenderingTick;

        _metadata.Start();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderingTick;
        _displayRequest.Release();
        SaveCurrentMonitor();

        _artworkCts?.Cancel();
        _artwork.Dispose();
        _ = ShutdownMetadataAsync();
    }

    private async Task ShutdownMetadataAsync()
    {
        try
        {
            await _metadata.DisposeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] metadata service shutdown failed: {ex.Message}");
        }
    }

    private void PositionOnRememberedMonitor()
    {
        try
        {
            if (!string.IsNullOrEmpty(_settings.LastMonitorDeviceName))
            {
                var rect = MonitorInterop.GetMonitorRectByDeviceName(_settings.LastMonitorDeviceName);
                if (rect is { } r)
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    Left = r.Left / dpi.DpiScaleX;
                    Top = r.Top / dpi.DpiScaleY;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] monitor restore failed: {ex.Message}");
        }
        finally
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveCurrentMonitor()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var device = MonitorInterop.GetDeviceNameForWindow(hwnd);
            if (!string.IsNullOrEmpty(device))
            {
                _settings.LastMonitorDeviceName = device;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] saving monitor failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Layout - proportions match the Apple Music fullscreen player rather
    // than pixel dimensions, so this recomputes on every resize.
    // ------------------------------------------------------------------

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => ApplyProportionalLayout();

    private void ApplyProportionalLayout()
    {
        var height = ActualHeight;
        if (height <= 0) return;

        var artSize = height * 0.45;
        ArtworkLayerA.Width = artSize;
        ArtworkLayerA.Height = artSize;
        ArtworkLayerB.Width = artSize;
        ArtworkLayerB.Height = artSize;

        var cornerRadius = artSize * 0.015;
        ArtworkLayerA.CornerRadius = new CornerRadius(cornerRadius);
        ArtworkLayerB.CornerRadius = new CornerRadius(cornerRadius);

        TitleText.FontSize = Math.Max(18.0, height * 0.022);
        SubtitleText.FontSize = Math.Max(13.0, height * 0.016);

        var maxTextWidth = Math.Max(artSize, Math.Min(ActualWidth * 0.8, artSize * 1.6));
        TitleText.MaxWidth = maxTextWidth;
        SubtitleText.MaxWidth = maxTextWidth;
        ProgressRow.Width = maxTextWidth;
        // TransportRow's outer columns are both "*" so the transport buttons
        // stay centred regardless of the (unequal) icon counts either side -
        // that only works once the Grid actually has a width to distribute.
        TransportRow.Width = maxTextWidth;
    }

    // ------------------------------------------------------------------
    // Keyboard / transport
    // ------------------------------------------------------------------

    private async void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Space:
                await _metadata.TryPlayPauseAsync();
                break;
            case Key.Left:
                await _metadata.TrySkipPreviousAsync();
                break;
            case Key.Right:
                await _metadata.TrySkipNextAsync();
                break;
            case Key.F:
                ToggleFullscreen();
                break;
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            Width = 900;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        _isFullscreen = !_isFullscreen;
    }

    // These control the sending iPhone via SMTC, not local playback - they
    // simply no-op (return false) when nothing is connected.
    private async void OnPlayPauseClick(object sender, RoutedEventArgs e) => await _metadata.TryPlayPauseAsync();

    private async void OnPreviousClick(object sender, RoutedEventArgs e) => await _metadata.TrySkipPreviousAsync();

    private async void OnNextClick(object sender, RoutedEventArgs e) => await _metadata.TrySkipNextAsync();

    // ------------------------------------------------------------------
    // MetadataService events
    // ------------------------------------------------------------------

    private void OnTrackChanged(object? sender, TrackInfo track)
    {
        _ = Dispatcher.InvokeAsync(() => HandleTrackChangedAsync(track));
    }

    private static readonly Geometry PlayIconGeometry = CreateFrozenGeometry("M5,3 L19,12 L5,21 Z");
    private static readonly Geometry PauseIconGeometry = CreateFrozenGeometry("M6,4 H10 V20 H6 Z M14,4 H18 V20 H14 Z");

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void OnPlaybackUpdated(object? sender, PlaybackSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _progress.Update(snapshot);
            PlayPauseIcon.Data = snapshot.Status == PlaybackStatus.Playing ? PauseIconGeometry : PlayIconGeometry;
        });
    }

    private void OnTrackedSessionChanged(object? sender, string? appId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Debug.WriteLine($"[MainWindow] tracking SMTC session: {appId ?? "(none)"}");
            var hasSession = appId is not null;
            PrevButton.IsEnabled = hasSession;
            PlayPauseButton.IsEnabled = hasSession;
            NextButton.IsEnabled = hasSession;
        });
    }

    // ------------------------------------------------------------------
    // Artwork + palette pipeline
    // ------------------------------------------------------------------

    private async Task HandleTrackChangedAsync(TrackInfo track)
    {
        // Cancel any resolution still in flight for a previous track - on a
        // rapid skip, a slow stale lookup must never win the crossfade race
        // against the track actually playing now.
        _artworkCts?.Cancel();
        var cts = new CancellationTokenSource();
        _artworkCts = cts;

        TitleText.Text = track.HasTitle ? track.Title : string.Empty;
        SubtitleText.Text = track.HasArtist ? track.Artist : string.Empty;

        try
        {
            var result = await _artwork.ResolveAsync(track, () => _metadata.GetCurrentThumbnailAsync(), cts.Token);
            if (cts.IsCancellationRequested) return;

            // "Artist — Album" when the lookup resolved one, else artist
            // alone with no dash - ShairportQt never sends an album name.
            SubtitleText.Text = !string.IsNullOrEmpty(result.ResolvedAlbum)
                ? $"{track.Artist} — {result.ResolvedAlbum}"
                : track.Artist;

            byte[]? imageBytes = null;
            if (result.LocalPath is not null && File.Exists(result.LocalPath))
            {
                imageBytes = await File.ReadAllBytesAsync(result.LocalPath, cts.Token);
            }

            if (cts.IsCancellationRequested) return;

            await CrossfadeArtworkAsync(imageBytes, result.Source, cts.Token);
            if (cts.IsCancellationRequested) return;

            var palette = _palette.ExtractPalette(imageBytes);
            MeshBackground.SetPalette(palette);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer track change - nothing to do
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] artwork/palette pipeline failed: {ex.Message}");
        }
    }

    private async Task CrossfadeArtworkAsync(byte[]? imageBytes, ArtworkSource source, CancellationToken token)
    {
        var incoming = _artworkLayerAIsFront ? ArtworkLayerB : ArtworkLayerA;
        var outgoing = _artworkLayerAIsFront ? ArtworkLayerA : ArtworkLayerB;

        BitmapImage? bitmap = null;
        if (imageBytes is not null)
        {
            bitmap = new BitmapImage();
            using var stream = new MemoryStream(imageBytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }

        incoming.Background = bitmap is not null
            ? new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28));

        // The SMTC fallback thumbnail is only 125px - blur it heavily rather
        // than render it upscaled and sharp.
        incoming.Effect = source == ArtworkSource.SmtcThumbnail ? new BlurEffect { Radius = 22 } : null;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
        incoming.BeginAnimation(OpacityProperty, fadeIn);
        outgoing.BeginAnimation(OpacityProperty, fadeOut);

        _artworkLayerAIsFront = !_artworkLayerAIsFront;

        await Task.Delay(400, token);
    }

    // ------------------------------------------------------------------
    // Progress bar - interpolated locally; see ProgressInterpolator.
    // ------------------------------------------------------------------

    private void OnProgressTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressVisual();

    private void OnRenderingTick(object? sender, EventArgs e) => UpdateProgressVisual();

    private void UpdateProgressVisual()
    {
        if (!_progress.HasDuration)
        {
            ProgressRow.Visibility = Visibility.Hidden;
            return;
        }
        ProgressRow.Visibility = Visibility.Visible;

        var position = _progress.GetPosition(DateTime.UtcNow);
        var duration = _progress.Duration;
        var fraction = duration.TotalSeconds > 0
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0.0, 1.0)
            : 0.0;

        ProgressFill.Width = ProgressTrack.ActualWidth * fraction;

        var remaining = duration - position;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        ElapsedText.Text = FormatTime(position);
        RemainingText.Text = "-" + FormatTime(remaining);
    }

    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
}
